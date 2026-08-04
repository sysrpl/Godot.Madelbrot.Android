using Godot;
using System;
using System.Collections.Generic;

public partial class Main : Node3D
{
	// A minimal double-precision 2D vector. Godot's own Vector2 is single-precision, which
	// isn't enough to track the pan center once zoomed in deep enough that a screen-pixel's
	// worth of movement is smaller than float32 can represent relative to that center.
	private struct DVector2
	{
		public double X;
		public double Y;

		public DVector2(double x, double y)
		{
			X = x;
			Y = y;
		}

		public static DVector2 operator +(DVector2 a, DVector2 b) => new(a.X + b.X, a.Y + b.Y);
		public static DVector2 operator -(DVector2 a, DVector2 b) => new(a.X - b.X, a.Y - b.Y);
		public static DVector2 operator *(DVector2 a, double s) => new(a.X * s, a.Y * s);
	}

	private const float ZoomAnimationSeconds = 0.3f;
	private const float WheelZoomFactor = 1.25f;
	// Kept well above 1/ZoomAnimationSeconds so a single idle-rate frame can never carry a
	// delta large enough to complete the whole zoom animation in one step (which is exactly
	// what happened at 5 FPS, since 1/5s == ZoomAnimationSeconds).
	private const int IdleFps = 5;
	private const int ActiveFps = 60;
	private const double UnthrottleSeconds = 2.0;
	// Tour leg timing. Each leg cycles through five phases, A-E:
	//
	//   Phase | Description           | Start   | Duration
	//   ------|-----------------------|---------|---------
	//   A     | Hold on current POI   | 0.00s   | 0.25s
	//   B     | Zoom out              | 0.25s   | 4.75s
	//   C     | Pan                   | 5.00s   | 1.50s
	//   D     | Zoom in               | 6.50s   | 4.75s
	//   E     | Hold at new POI       | 11.25s  | 0.25s
	//
	// Total leg duration: 11.50s, then the cycle repeats at the next POI's (A).
	private const double TourHoldSeconds = 0.25;      // (A), (E)
	private const double TourZoomLegSeconds = 4.75;   // (B), (D)
	private const double TourPanLegSeconds = 1.5;     // (C)
	private const float TourOverviewZoom = 2.0f;
	private const float MouseRotationDegreesPerPixel = 0.25f;

	// Rotation's fall from 180 to 0 spans (A)+(B); its rise from 0 to 180 spans (D)+(E).
	// Both pairs are one hold plus one zoom leg, so they share this duration.
	private const double TourRotationTransitionSeconds = TourHoldSeconds + TourZoomLegSeconds;

	// Documented, named locations in the Mandelbrot set (center coordinate sourced from
	// mrob.com's Mu-Ency encyclopedia and the sci.fractals FAQ; zoom level chosen to frame
	// each feature). Coordinates with a published magnification convert as zoom = 3 / width.
	private static readonly (Vector2 Pan, float Zoom)[] TourPoints =
	{
		(new Vector2(0.27205f, 0.006118f), 70.0f),      // Elephant Valley
		(new Vector2(-0.7451968f, 0.1018699f), 38.0f),  // Seahorse Valley
		(new Vector2(-0.74548f, 0.11669f), 470.0f),     // Seahorse Valley cusp
		(new Vector2(-0.0875937f, 0.6550903f), 100.0f), // Triple Spiral Valley
		(new Vector2(-1.36f, 0.005f), 90.0f),           // Scepter Valley
		(new Vector2(-1.401155f, 0.0f), 120.0f),        // Myrberg-Feigenbaum point
		(new Vector2(-1.75f, 0.0f), 60.0f),             // Mini-Mandelbrot at the period-3 bulb
	};

	// On Android, tweening the manual pinch-zoom is extra render load for little benefit on
	// top of already-limited GPU headroom, so it jumps straight to the target instead.
	private static readonly bool IsAndroid = OS.GetName() == "Android";

	private ShaderMaterial _shaderMaterial;
	private DVector2 _panOffset = new(-0.5, 0.0);
	private double _zoom = 1.0;
	private float _rotation = 0.0f;
	private DVector2 _targetPanOffset = new(-0.5, 0.0);
	private double _targetZoom = 1.0;
	private bool _dragging = false;
	private Tween _zoomTween;
	private ulong _lastInteractionTicksMsec;
	private bool _touring = false;
	private int _tourIndex = 0;
	private Tween _tourTween;
	private Tween _tourRotationTween;
	private Button _tourButton;
	private readonly Dictionary<int, Vector2> _touches = new();
	private float _lastPinchDistance;
	private float _lastTouchRotationAngle;
	private bool _rotatingWithMouse = false;
	private Vector2 _rotationDragOrigin;
	private float _rotationDragBaselineValue;

	public override void _Ready()
	{
		var ui = GetNode<CanvasLayer>("UI");

		_shaderMaterial = new ShaderMaterial
		{
			Shader = GD.Load<Shader>("res://resources/shaders/mandelbrot.gdshader")
		};

		var gradientRect = new ColorRect
		{
			Name = "GradientRect",
			Material = _shaderMaterial
		};
		gradientRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		ui.AddChild(gradientRect);
		ui.MoveChild(gradientRect, 0);

		UpdateShaderUniforms();

		OS.LowProcessorUsageMode = true;
		UpdateFramerate();

		_tourButton = new Button
		{
			Name = "TourButton",
			Text = "Tour",
			Position = new Vector2(20, 20),
			Size = new Vector2(140, 60)
		};
		_tourButton.AddThemeFontSizeOverride("font_size", 28);
		_tourButton.Pressed += OnTourPressed;
		ui.AddChild(_tourButton);

		var quitButton = new Button
		{
			Name = "QuitButton",
			Text = "Quit",
			Position = new Vector2(20, 90),
			Size = new Vector2(140, 60)
		};
		quitButton.AddThemeFontSizeOverride("font_size", 28);
		quitButton.Pressed += OnQuitPressed;
		ui.AddChild(quitButton);
	}

	public override void _Process(double delta)
	{
		UpdateFramerate();
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo && keyEvent.Keycode == Key.F2)
		{
			TakeScreenshot();
		}

		if (_touring)
		{
			return;
		}

		if (@event is InputEventMouseButton mouseButton)
		{
			if (mouseButton.ButtonIndex == MouseButton.Left)
			{
				if (mouseButton.Pressed)
				{
					if (mouseButton.DoubleClick)
					{
						ResetRotation();
					}
					else if (mouseButton.CtrlPressed)
					{
						_rotatingWithMouse = true;
						_rotationDragOrigin = mouseButton.Position;
						_rotationDragBaselineValue = _rotation;
					}
					else
					{
						_dragging = true;
					}
				}
				else
				{
					_dragging = false;
					_rotatingWithMouse = false;
				}
			}
			else if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.WheelUp)
			{
				Zoom(mouseButton.Position, WheelZoomFactor);
			}
			else if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.WheelDown)
			{
				Zoom(mouseButton.Position, 1.0f / WheelZoomFactor);
			}
		}
		else if (@event is InputEventMouseMotion mouseMotion)
		{
			if (_rotatingWithMouse)
			{
				RotateFromMouse(mouseMotion.Position);
			}
			else if (_dragging)
			{
				Pan(mouseMotion.Relative);
			}
		}
		else if (@event is InputEventScreenTouch screenTouch)
		{
			if (screenTouch.Pressed && screenTouch.DoubleTap)
			{
				ResetRotation();
			}

			if (screenTouch.Pressed)
			{
				_touches[screenTouch.Index] = screenTouch.Position;
			}
			else
			{
				_touches.Remove(screenTouch.Index);
			}

			// Re-baseline the pinch distance whenever exactly two fingers are down,
			// whether we just gained a second finger or dropped back to two from a third.
			if (_touches.Count == 2)
			{
				_lastPinchDistance = GetPinchDistance();
			}
			else if (_touches.Count >= 3)
			{
				_lastTouchRotationAngle = GetTouchRotationAngle();
			}
		}
		else if (@event is InputEventScreenDrag screenDrag)
		{
			_touches[screenDrag.Index] = screenDrag.Position;

			if (_touches.Count == 1)
			{
				Pan(screenDrag.Relative);
			}
			else if (_touches.Count == 2)
			{
				float distance = GetPinchDistance();
				if (_lastPinchDistance > 0.0001f)
				{
					Zoom(GetPinchMidpoint(), distance / _lastPinchDistance);
				}
				_lastPinchDistance = distance;
			}
			else if (_touches.Count >= 3)
			{
				// Three or more fingers rotate instead of panning/zooming: track the angle of
				// the line between the two lowest-indexed touches and accumulate its change.
				float angle = GetTouchRotationAngle();
				float deltaDeg = Mathf.RadToDeg(Mathf.Wrap(angle - _lastTouchRotationAngle, -Mathf.Pi, Mathf.Pi));
				_rotation += deltaDeg;
				UpdateShaderUniforms();
				_lastTouchRotationAngle = angle;
				MarkInteraction();
			}
		}
	}

	private float GetPinchDistance()
	{
		var positions = new List<Vector2>(_touches.Values);
		return positions[0].DistanceTo(positions[1]);
	}

	private Vector2 GetPinchMidpoint()
	{
		var positions = new List<Vector2>(_touches.Values);
		return (positions[0] + positions[1]) / 2.0f;
	}

	// The two lowest-indexed touches define a consistent reference line for rotation,
	// regardless of how many additional fingers (3, 4, 5...) are also down.
	private float GetTouchRotationAngle()
	{
		var sortedKeys = new List<int>(_touches.Keys);
		sortedKeys.Sort();
		Vector2 a = _touches[sortedKeys[0]];
		Vector2 b = _touches[sortedKeys[1]];
		return (b - a).Angle();
	}

	// Rotates based purely on horizontal movement from the fixed mouse-down origin: moving
	// right rotates clockwise, moving left rotates counter-clockwise, at a fixed rate.
	private void RotateFromMouse(Vector2 currentPosition)
	{
		float deltaX = currentPosition.X - _rotationDragOrigin.X;
		_rotation = _rotationDragBaselineValue + deltaX * MouseRotationDegreesPerPixel;
		UpdateShaderUniforms();
		MarkInteraction();
	}

	private void ResetRotation()
	{
		SetRotationValue(0.0f);
		MarkInteraction();
	}

	// Rotates a screen-space uv vector by the current view rotation, using the exact same
	// transform the shader applies to UV before mapping it into the complex plane. Pan and
	// zoom-anchor math both work in screen-space deltas, so both need this compensation to
	// stay correct once the view is rotated - otherwise dragging "right" would still shift
	// the fractal along its unrotated real axis instead of the axis that looks right on screen.
	private DVector2 RotateUvByCurrentRotation(DVector2 uv)
	{
		float rad = Mathf.DegToRad(_rotation);
		float co = Mathf.Cos(rad);
		float s = Mathf.Sin(rad);
		return new DVector2(uv.X * co + uv.Y * s, -uv.X * s + uv.Y * co);
	}

	private void Pan(Vector2 deltaPixels)
	{
		// Dragging takes over immediately, so snap any in-flight zoom animation to its target first.
		_zoomTween?.Kill();
		_zoom = _targetZoom;
		_panOffset = _targetPanOffset;

		Vector2 size = GetViewport().GetVisibleRect().Size;
		double aspect = (double)size.X / size.Y;

		DVector2 deltaUv = new(deltaPixels.X / size.X * aspect, deltaPixels.Y / size.Y);
		deltaUv = RotateUvByCurrentRotation(deltaUv);

		double scale = 3.0 / _zoom;
		_panOffset -= deltaUv * scale;
		_targetPanOffset = _panOffset;

		UpdateShaderUniforms();
		MarkInteraction();
	}

	private void Zoom(Vector2 mousePosition, float factor)
	{
		Vector2 size = GetViewport().GetVisibleRect().Size;
		double aspect = (double)size.X / size.Y;

		DVector2 uv = new((mousePosition.X / size.X - 0.5) * aspect, mousePosition.Y / size.Y - 0.5);
		uv = RotateUvByCurrentRotation(uv);

		double oldScale = 3.0 / _targetZoom;
		_targetZoom = Math.Max(_targetZoom * factor, 1.0);
		double newScale = 3.0 / _targetZoom;

		_targetPanOffset += uv * (oldScale - newScale);

		AnimateZoomTo(_targetZoom, _targetPanOffset);
		MarkInteraction();
	}

	private void AnimateZoomTo(double targetZoom, DVector2 targetPanOffset)
	{
		_zoomTween?.Kill();

		if (IsAndroid)
		{
			_zoom = targetZoom;
			_panOffset = targetPanOffset;
			UpdateShaderUniforms();
			return;
		}

		_zoomTween = CreateTween();
		_zoomTween.SetParallel(true);
		TweenZoomTo(_zoomTween, targetZoom, ZoomAnimationSeconds);
		TweenPanTo(_zoomTween, targetPanOffset, ZoomAnimationSeconds);
	}

	// Godot's Tween only marshals float/Vector2 (single precision) through Callable, which
	// would round our double-precision pan/zoom back down to float32 every frame. Instead we
	// let the Tween drive a plain 0..1 progress value (so it still owns the easing curve),
	// and do the actual value interpolation ourselves in double precision.
	//
	// The "from" value is captured lazily on the tweener's first callback rather than up front
	// when this method is called, since a chained (sequential) tweener doesn't actually start
	// running until earlier legs finish - capturing eagerly would grab the pre-tour value
	// instead of wherever the previous leg actually left off, causing a jump at the hand-off.
	private void TweenZoomTo(Tween tween, double targetZoom, double durationSeconds)
	{
		bool started = false;
		double fromZoom = 0.0;
		tween.TweenMethod(Callable.From<float>(t =>
		{
			if (!started)
			{
				started = true;
				fromZoom = _zoom;
			}
			_zoom = fromZoom + (targetZoom - fromZoom) * t;
			UpdateShaderUniforms();
		}), 0.0f, 1.0f, durationSeconds).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
	}

	private void TweenPanTo(Tween tween, DVector2 targetPan, double durationSeconds)
	{
		bool started = false;
		DVector2 fromPan = default;
		tween.TweenMethod(Callable.From<float>(t =>
		{
			if (!started)
			{
				started = true;
				fromPan = _panOffset;
			}
			_panOffset = fromPan + (targetPan - fromPan) * t;
			UpdateShaderUniforms();
		}), 0.0f, 1.0f, durationSeconds).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
	}

	// Resets the "recently interacted" window so the framerate stays unthrottled
	// for UnthrottleSeconds after the most recent pan or zoom.
	private void MarkInteraction()
	{
		_lastInteractionTicksMsec = Time.GetTicksMsec();
		UpdateFramerate();
	}

	// Renders at ActiveFps for UnthrottleSeconds after the last pan/zoom (or continuously
	// while touring), and drops to IdleFps otherwise, to avoid burning power redrawing a
	// static Mandelbrot view.
	private void UpdateFramerate()
	{
		double elapsedSeconds = (Time.GetTicksMsec() - _lastInteractionTicksMsec) / 1000.0;
		bool active = _touring || elapsedSeconds < UnthrottleSeconds;
		Engine.MaxFps = active ? ActiveFps : IdleFps;
	}

	private void OnTourPressed()
	{
		_touring = !_touring;
		_tourButton.Text = _touring ? "Stop" : "Tour";

		if (_touring)
		{
			StartTour();
		}
		else
		{
			StopTour();
		}
	}

	private void StartTour()
	{
		_dragging = false;
		_rotatingWithMouse = false;
		_touches.Clear();
		_lastPinchDistance = 0.0f;

		// A manual zoom might still be mid-animation; snap it to its target so it
		// doesn't keep fighting the tour tween over _zoom/_panOffset.
		_zoomTween?.Kill();
		_zoom = _targetZoom;
		_panOffset = _targetPanOffset;
		SetRotationValue(0.0f);

		_tourIndex = 0;
		MarkInteraction();
		PlayTourStep();
	}

	private void StopTour()
	{
		_tourTween?.Kill();
		_tourRotationTween?.Kill();

		// Keep the manual pan/zoom targets in sync with wherever the tour left off.
		_targetZoom = _zoom;
		_targetPanOffset = _panOffset;
		SetRotationValue(0.0f);
		MarkInteraction();
	}

	private void PlayTourStep()
	{
		var (targetPan, targetZoom) = TourPoints[_tourIndex];
		DVector2 targetPanD = new(targetPan.X, targetPan.Y);

		_tourTween?.Kill();
		_tourTween = CreateTween();

		// (A) Hold on the current point of interest before departing.
		_tourTween.TweenInterval(TourHoldSeconds);

		// (B) Zoom out to a wide overview.
		TweenZoomTo(_tourTween, TourOverviewZoom, TourZoomLegSeconds);

		// (C) Pan to center the point of interest.
		TweenPanTo(_tourTween, targetPanD, TourPanLegSeconds);

		// (D) Zoom in to the point of interest's best view.
		TweenZoomTo(_tourTween, targetZoom, TourZoomLegSeconds);

		// (E) Hold at the new point of interest.
		_tourTween.TweenInterval(TourHoldSeconds);

		// Rotation runs on its own timeline, independent of the zoom/pan phases: starts at 180
		// and eases to 0 across the combined span of (A) and (B), holds at 0 through (C), then
		// eases back to 180 across the combined span of (D) and (E).
		SetRotationValue(180.0f);

		_tourRotationTween?.Kill();
		_tourRotationTween = CreateTween();
		_tourRotationTween.TweenMethod(Callable.From<float>(SetRotationValue), 180.0f, 0.0f, TourRotationTransitionSeconds) // (A)+(B) fall to 0
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		_tourRotationTween.TweenInterval(TourPanLegSeconds); // (C) hold at 0
		_tourRotationTween.TweenMethod(Callable.From<float>(SetRotationValue), 0.0f, 180.0f, TourRotationTransitionSeconds) // (D)+(E) rise to 180
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);

		_tourTween.Finished += OnTourStepFinished;
	}

	private void OnTourStepFinished()
	{
		if (!_touring)
		{
			return;
		}

		_tourIndex = (_tourIndex + 1) % TourPoints.Length;
		PlayTourStep();
	}

	private void SetRotationValue(float degrees)
	{
		_rotation = degrees;
		UpdateShaderUniforms();
	}

	private void UpdateShaderUniforms()
	{
		_shaderMaterial.SetShaderParameter("pan_offset", new Vector2((float)_panOffset.X, (float)_panOffset.Y));
		_shaderMaterial.SetShaderParameter("zoom", (float)_zoom);
		_shaderMaterial.SetShaderParameter("rotation", Mathf.DegToRad(_rotation));
	}

	private void TakeScreenshot()
	{
		Image image = GetViewport().GetTexture().GetImage();
		string path = ProjectSettings.GlobalizePath("res://docs/snapshot.png");
		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
		image.SavePng(path);
	}

	private void OnQuitPressed()
	{
		GetTree().Quit();
	}
}
