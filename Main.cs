using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

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

    // A single user-defined (or default) tour stop. Stored as JSON in user:// so it survives
    // across runs and can be hand-edited too.
    private class TourPointData
    {
        public double PanX { get; set; }
        public double PanY { get; set; }
        public double Zoom { get; set; }
        public float Rotation { get; set; }
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

    private const string TourPointsFilePath = "user://tour_points.json";

    // Documented, named locations in the Mandelbrot set (center coordinate sourced from
    // mrob.com's Mu-Ency encyclopedia and the sci.fractals FAQ; zoom level chosen to frame
    // each feature). Coordinates with a published magnification convert as zoom = 3 / width.
    // Rotation is 180 on every default point to reproduce the tour's original fixed
    // 180 -> 0 -> 180 oscillation exactly when the user resets back to these.
    private static List<TourPointData> CreateMrRobsTourPoints() => new()
    {
        new TourPointData { PanX = 0.27205, PanY = 0.006118, Zoom = 70.0, Rotation = 180.0f },      // Elephant Valley
		new TourPointData { PanX = -0.7451968, PanY = 0.1018699, Zoom = 38.0, Rotation = 180.0f },  // Seahorse Valley
		new TourPointData { PanX = -0.74548, PanY = 0.11669, Zoom = 470.0, Rotation = 180.0f },     // Seahorse Valley cusp
		new TourPointData { PanX = -0.0875937, PanY = 0.6550903, Zoom = 100.0, Rotation = 180.0f }, // Triple Spiral Valley
		new TourPointData { PanX = -1.36, PanY = 0.005, Zoom = 90.0, Rotation = 180.0f },           // Scepter Valley
		new TourPointData { PanX = -1.401155, PanY = 0.0, Zoom = 120.0, Rotation = 180.0f },        // Myrberg-Feigenbaum point
		new TourPointData { PanX = -1.75, PanY = 0.0, Zoom = 60.0, Rotation = 180.0f },             // Mini-Mandelbrot at the period-3 bulb
	};

    // The default tour points can be generated using F3
    private static List<TourPointData> CreateDefaultTourPoints() =>
    [
        new TourPointData { PanX = 0.255946973265845, PanY = -0.0006764946919572745, Zoom = 213821.2564619854, Rotation = -407.25f },
        new TourPointData { PanX = -0.7069393371540275, PanY = -0.2545913744314121, Zoom = 44094.60488585555, Rotation = -85.36312f },
        new TourPointData { PanX = -0.8661799278981501, PanY = -0.2453466942681265, Zoom = 6260.229563940328, Rotation = 136.23889f },
        new TourPointData { PanX = -0.7318089526149906, PanY = -0.2403350653664959, Zoom = 18367.09923159824, Rotation = -495.0f },
        new TourPointData { PanX = -0.7538183300193422, PanY = -0.0494593949919514, Zoom = 1009.741958682895, Rotation = -262.0f },
        new TourPointData { PanX = -1.7877680347542297, PanY = 1.933683303690229E-06, Zoom = 22958.887724057102, Rotation = 359.5f },
        new TourPointData { PanX = -1.7694637477326083, PanY = 0.0031169525620488247, Zoom = 28698.620346138505, Rotation = 144.0f },
        new TourPointData { PanX = -1.7574172012020959, PanY = 0.016310496047429634, Zoom = 22958.908593025724, Rotation = -321.25f },
        new TourPointData { PanX = -1.7607379635598965, PanY = -0.012080752770290911, Zoom = 35873.31498965067, Rotation = -126.0f },
    ];

    // On Android, tweening the manual pinch-zoom is extra render load for little benefit on
    // top of already-limited GPU headroom, so it jumps straight to the target instead.
    internal static readonly bool IsAndroid = OS.GetName() == "Android";

    // Every resources/buttons/{prefix}_{state}.svg is a complete, self-contained 350x90
    // button graphic (background, border, icon and caption all baked in), so these load as
    // whole per-state textures via TextureButton rather than a Button + theme stylebox/font.
    private const float ButtonSvgAspect = 90f / 350f;

    // Scales the main Tour/Mark/Edit/Quit button stack (top-left of the main window). Tweak
    // this one value - UpdateButtonLayout() derives its stacking spacing from the resulting
    // button size, so it doesn't need separate adjustment.
    private const float MainButtonScale = 0.75f;

    private static TextureButton CreateIconButton(string name, string prefix, float width)
    {
        var size = new Vector2(width, width * ButtonSvgAspect);
        return new TextureButton
        {
            Name = name,
            TextureNormal = GD.Load<Texture2D>($"res://resources/buttons/{prefix}_normal.svg"),
            TextureHover = GD.Load<Texture2D>($"res://resources/buttons/{prefix}_hover.svg"),
            TexturePressed = GD.Load<Texture2D>($"res://resources/buttons/{prefix}_pressed.svg"),
            TextureDisabled = GD.Load<Texture2D>($"res://resources/buttons/{prefix}_disabled.svg"),
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.Scale,
            CustomMinimumSize = size,
            Size = size
        };
    }

    private void SetTourButtonTouring(bool touring)
    {
        string prefix = touring ? "stop" : "tour";
        _tourButton.TextureNormal = GD.Load<Texture2D>($"res://resources/buttons/{prefix}_normal.svg");
        _tourButton.TextureHover = GD.Load<Texture2D>($"res://resources/buttons/{prefix}_hover.svg");
        _tourButton.TexturePressed = GD.Load<Texture2D>($"res://resources/buttons/{prefix}_pressed.svg");
        _tourButton.TextureDisabled = GD.Load<Texture2D>($"res://resources/buttons/{prefix}_disabled.svg");
    }

    private ShaderMaterial _mandelbrotShader;
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
    private TextureButton _tourButton;
    private TextureButton _quitButton;
    private TextureButton _markButton;
    private TextureButton _editButton;
    private Label _statsLabel;
    private List<TourPointData> _tourPoints = new();
    private DraggableWindow _pointsWindow;
    private ScrollContainer _pointsScrollContainer;
    private float _pointsScrollRemainder;
    private VBoxContainer _pointsListContainer;
    private readonly List<ColorRect> _dropGaps = new();
    private int? _manualDragIndex;
    private int? _manualDragTargetIndex;
    private Label _manualDragPreview;
    private readonly Dictionary<int, Vector2> _touches = new();
    private float _lastPinchDistance;
    private float _lastTouchRotationAngle;
    private bool _rotatingWithMouse = false;
    private Vector2 _rotationDragOrigin;
    private float _rotationDragBaselineValue;

    public override void _Ready()
    {
        var ui = GetNode<CanvasLayer>("UI");

        // Set explicitly on every Control rather than relying solely on the project's default
        // theme setting (gui/theme/custom), since CanvasLayer isn't a Control and can't
        // propagate a theme down to the buttons parented under it.
        var theme = GD.Load<Theme>("res://resources/themes/minimal.tres");

        _mandelbrotShader = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://resources/shaders/mandelbrot.gdshader")
        };

        var renderArea = new ColorRect
        {
            Name = "RenderArea",
            Material = _mandelbrotShader
        };
        renderArea.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        ui.AddChild(renderArea);
        ui.MoveChild(renderArea, 0);

        UpdateShaderUniforms();

        OS.LowProcessorUsageMode = true;
        UpdateFramerate();

        _tourButton = CreateIconButton("TourButton", "tour", 280f * MainButtonScale);
        _tourButton.Position = new Vector2(20, 20);
        _tourButton.Pressed += OnTourPressed;
        ui.AddChild(_tourButton);

        _markButton = CreateIconButton("MarkButton", "mark", 280f * MainButtonScale);
        _markButton.Pressed += MarkTourPoint;
        ui.AddChild(_markButton);

        _editButton = CreateIconButton("EditButton", "edit", 280f * MainButtonScale);
        _editButton.Pressed += OnPointsButtonPressed;
        ui.AddChild(_editButton);

        _quitButton = CreateIconButton("QuitButton", "quit", 280f * MainButtonScale);
        _quitButton.Pressed += OnQuitPressed;
        ui.AddChild(_quitButton);

        _statsLabel = new Label
        {
            Name = "StatsLabel",
            Theme = theme,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statsLabel.AddThemeFontSizeOverride("font_size", IsAndroid ? 20 : 14);
        _statsLabel.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.85f));
        _statsLabel.AddThemeConstantOverride("shadow_offset_x", 0);
        _statsLabel.AddThemeConstantOverride("shadow_offset_y", 0);
        _statsLabel.AddThemeConstantOverride("shadow_outline_size", 6);
        _statsLabel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _statsLabel.OffsetTop = -36;
        _statsLabel.OffsetBottom = 0;
        _statsLabel.OffsetLeft = 20;
        _statsLabel.OffsetRight = -20;
        ui.AddChild(_statsLabel);

        LoadTourPoints();
        BuildPointsDialog(theme);
        UpdateButtonLayout();

        GetTree().Root.SizeChanged += OnScreenSizeChanged;
    }

    // On Android, an orientation change resizes the root window/viewport (portrait <->
    // landscape), which is what this reacts to. Resizes the dialog to 80% of the new width
    // first, then centers it - centering has to happen second since it depends on the
    // dialog's (new) size.
    private void OnScreenSizeChanged()
    {
        if (!IsAndroid || _pointsWindow == null || !_pointsWindow.Visible)
        {
            return;
        }

        Vector2I viewportSize = (Vector2I)GetViewport().GetVisibleRect().Size;
        _pointsWindow.Size = new Vector2I((int)(viewportSize.X * 0.8f), _pointsWindow.Size.Y);
        _pointsWindow.Position = (viewportSize - _pointsWindow.Size) / 2;
    }

    // Idle: Tour, Mark, Edit, Quit stacked in that order. While touring: Mark/Edit hide
    // (marking/editing points mid-tour doesn't make sense) and Quit moves up to sit
    // directly below Tour.
    private void UpdateButtonLayout()
    {
        _markButton.Visible = !_touring;
        _editButton.Visible = !_touring;

        float step = _tourButton.Size.Y + 8f;
        _markButton.Position = new Vector2(20, 20 + step);
        _editButton.Position = new Vector2(20, 20 + step * 2);
        _quitButton.Position = _touring ? new Vector2(20, 20 + step) : new Vector2(20, 20 + step * 3);
    }

    public override void _Process(double delta)
    {
        UpdateFramerate();
        UpdateStatsLabel();

        if (_pointsWindow != null && _pointsWindow.Visible && !_pointsWindow.GuiIsDragging() && _manualDragIndex == null)
        {
            // Safety net: clears any lit drop gap regardless of how the drag ended (dropped
            // on a row, dropped outside the list, cancelled), since that's simpler than trying
            // to catch every one of those cases individually. GuiIsDragging() only reflects
            // Godot's own engine-tracked (mouse) drag state, not our manual touch-drag path,
            // so _manualDragIndex has to be checked too or this fires every frame during a
            // touch drag and clears the indicator right after ShowDropIndicator sets it.
            ClearDropGaps();
        }
    }

    public override void _Input(InputEvent input)
    {
        if (input is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.F1)
            {
                DisplayServer.WindowSetMode(DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen
                    ? DisplayServer.WindowMode.Windowed
                    : DisplayServer.WindowMode.Fullscreen);
            }
            else if (keyEvent.Keycode == Key.F2)
                TakeScreenshot();
            else if (keyEvent.Keycode == Key.F3)
                ExportTourPointsAsCode();
            else if (keyEvent.Keycode == Key.Escape)
            {
                if (_pointsWindow.Visible)
                    _pointsWindow.Hide();
                else
                    GetTree().Quit();
                return;
            }
        }

        if (_touring)
            return;

        if (input is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (mouseButton.Pressed)
                {
                    if (mouseButton.DoubleClick)
                        ResetRotation();
                    else if (mouseButton.CtrlPressed)
                    {
                        _rotatingWithMouse = true;
                        _rotationDragOrigin = mouseButton.Position;
                        _rotationDragBaselineValue = _rotation;
                    }
                    else
                        _dragging = true;
                }
                else
                {
                    _dragging = false;
                    _rotatingWithMouse = false;
                }
            }
            else if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.WheelUp)
                Zoom(mouseButton.Position, WheelZoomFactor);
            else if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.WheelDown)
                Zoom(mouseButton.Position, 1.0f / WheelZoomFactor);
        }
        else if (input is InputEventMouseMotion mouseMotion)
        {
            if (_rotatingWithMouse)
                RotateFromMouse(mouseMotion.Position);
            else if (_dragging)
                Pan(mouseMotion.Relative);
        }
        else if (input is InputEventScreenTouch screenTouch)
        {
            if (screenTouch.Pressed && screenTouch.DoubleTap)
                ResetRotation();
            if (screenTouch.Pressed)
                _touches[screenTouch.Index] = screenTouch.Position;
            else
                _touches.Remove(screenTouch.Index);

            // Re-baseline the pinch distance whenever exactly two fingers are down,
            // whether we just gained a second finger or dropped back to two from a third.
            if (_touches.Count == 2)
                _lastPinchDistance = GetPinchDistance();
            else if (_touches.Count >= 3)
                _lastTouchRotationAngle = GetTouchRotationAngle();
        }
        else if (input is InputEventScreenDrag screenDrag)
        {
            _touches[screenDrag.Index] = screenDrag.Position;

            if (_touches.Count == 1)
                Pan(screenDrag.Relative);
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
        bool dialogOpen = _pointsWindow != null && _pointsWindow.Visible;
        bool active = _touring || dialogOpen || elapsedSeconds < UnthrottleSeconds;
        Engine.MaxFps = active ? ActiveFps : IdleFps;
    }

    private void UpdateStatsLabel()
    {
        double fps = Engine.GetFramesPerSecond();
        _statsLabel.Text = $"{fps:F0} fps  /  ({_panOffset.X:F4}, {_panOffset.Y:F4})  /  zoom {_zoom:F1}  /  rotation {_rotation:F1}°";
    }

    private void OnTourPressed()
    {
        _touring = !_touring;
        SetTourButtonTouring(_touring);

        if (_touring)
        {
            StartTour();
        }
        else
        {
            StopTour();
        }

        // StartTour() may revert _touring back to false (e.g. an empty points list), so
        // this reads the final state rather than assuming the toggle above stuck.
        UpdateButtonLayout();
    }

    private void StartTour()
    {
        if (_tourPoints.Count == 0)
        {
            _touring = false;
            SetTourButtonTouring(false);
            return;
        }

        _dragging = false;
        _rotatingWithMouse = false;
        _touches.Clear();
        _lastPinchDistance = 0.0f;

        // A manual zoom might still be mid-animation; snap it to its target so it doesn't
        // keep fighting the tour tween over _zoom/_panOffset. Rotation is left as-is (not
        // reset) so the first leg's fall-to-0 starts from wherever the user left it.
        _zoomTween?.Kill();
        _zoom = _targetZoom;
        _panOffset = _targetPanOffset;

        _tourIndex = 0;
        MarkInteraction();
        PlayTourStep();
    }

    private void StopTour()
    {
        _tourTween?.Kill();
        _tourRotationTween?.Kill();

        // Keep the manual pan/zoom targets in sync with wherever the tour left off. Rotation
        // is left as-is (not reset to 0) so stopping the tour keeps whatever orientation the
        // current point of interest was arrived at.
        _targetZoom = _zoom;
        _targetPanOffset = _panOffset;
        MarkInteraction();
    }

    private void PlayTourStep()
    {
        if (_tourPoints.Count == 0)
        {
            return;
        }

        TourPointData point = _tourPoints[_tourIndex];
        DVector2 targetPanD = new(point.PanX, point.PanY);
        double targetZoomD = point.Zoom;

        _tourTween?.Kill();
        _tourTween = CreateTween();

        // (A) Hold on the current point of interest before departing.
        _tourTween.TweenInterval(TourHoldSeconds);

        // (B) Zoom out to a wide overview.
        TweenZoomTo(_tourTween, TourOverviewZoom, TourZoomLegSeconds);

        // (C) Pan to center the point of interest.
        TweenPanTo(_tourTween, targetPanD, TourPanLegSeconds);

        // (D) Zoom in to the point of interest's best view.
        TweenZoomTo(_tourTween, targetZoomD, TourZoomLegSeconds);

        // (E) Hold at the new point of interest.
        _tourTween.TweenInterval(TourHoldSeconds);

        // Rotation runs on its own timeline, independent of the zoom/pan phases: eases from
        // wherever it currently is (the previous point's stored rotation) down to 0 across
        // the combined span of (A) and (B), holds at 0 through (C), then eases up to this
        // point's stored rotation across the combined span of (D) and (E).
        float rotationAtLegStart = _rotation;

        _tourRotationTween?.Kill();
        _tourRotationTween = CreateTween();
        _tourRotationTween.TweenMethod(Callable.From<float>(SetRotationValue), rotationAtLegStart, 0.0f, TourRotationTransitionSeconds) // (A)+(B) fall to 0
            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
        _tourRotationTween.TweenInterval(TourPanLegSeconds); // (C) hold at 0
        _tourRotationTween.TweenMethod(Callable.From<float>(SetRotationValue), 0.0f, point.Rotation, TourRotationTransitionSeconds) // (D)+(E) rise to this point's rotation
            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);

        _tourTween.Finished += OnTourStepFinished;
    }

    private void OnTourStepFinished()
    {
        if (!_touring || _tourPoints.Count == 0)
        {
            return;
        }

        _tourIndex = (_tourIndex + 1) % _tourPoints.Count;
        PlayTourStep();
    }

    private void LoadTourPoints()
    {
        string path = ProjectSettings.GlobalizePath(TourPointsFilePath);
        if (System.IO.File.Exists(path))
        {
            try
            {
                string json = System.IO.File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<List<TourPointData>>(json);
                if (loaded != null && loaded.Count > 0)
                {
                    _tourPoints = loaded;
                    return;
                }
            }
            catch (Exception)
            {
                // Corrupt or hand-edited-wrong: fall back to the defaults below.
            }
        }

        _tourPoints = CreateDefaultTourPoints();
    }

    private static readonly JsonSerializerOptions TourPointsJsonOptions = new() { WriteIndented = true };

    private void SaveTourPoints()
    {
        string path = ProjectSettings.GlobalizePath(TourPointsFilePath);
        string json = JsonSerializer.Serialize(_tourPoints, TourPointsJsonOptions);
        System.IO.File.WriteAllText(path, json);
    }

    private void ResetTourPointsToDefaults()
    {
        _tourPoints = CreateDefaultTourPoints();
        _tourIndex = 0;
        SaveTourPoints();
        RefreshPointsDialog();
    }

    // Captures the current view (pan, zoom, rotation) as a new tour stop, appended to the end.
    private void MarkTourPoint()
    {
        _tourPoints.Add(new TourPointData
        {
            PanX = _targetPanOffset.X,
            PanY = _targetPanOffset.Y,
            Zoom = _targetZoom,
            Rotation = _rotation
        });
        SaveTourPoints();
        RefreshPointsDialog();
    }

    private void OnPointsButtonPressed()
    {
        RefreshPointsDialog();

        Vector2I viewportSize = (Vector2I)GetViewport().GetVisibleRect().Size;
        _pointsWindow.Position = (viewportSize - _pointsWindow.Size) / 2;

        _pointsWindow.Show();
    }

    // Jumps directly to a stored point (no animation - this is a curation/preview action,
    // not part of the scripted tour), stopping the tour first if one is running.
    private void GoToTourPoint(int index)
    {
        if (index < 0 || index >= _tourPoints.Count)
        {
            return;
        }

        if (_touring)
        {
            _touring = false;
            SetTourButtonTouring(false);
            StopTour();
            UpdateButtonLayout();
        }

        TourPointData point = _tourPoints[index];
        _targetPanOffset = new DVector2(point.PanX, point.PanY);
        _targetZoom = point.Zoom;
        _panOffset = _targetPanOffset;
        _zoom = _targetZoom;
        SetRotationValue(point.Rotation);
        MarkInteraction();
    }

    // Moves the point at fromIndex so it ends up at toIndex (as measured before removal -
    // e.g. dropping "above" row 3 means toIndex=3, "below" row 3 means toIndex=4).
    private void ReorderTourPoint(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _tourPoints.Count)
        {
            return;
        }

        TourPointData moved = _tourPoints[fromIndex];
        _tourPoints.RemoveAt(fromIndex);

        if (toIndex > fromIndex)
        {
            toIndex--;
        }
        toIndex = Math.Clamp(toIndex, 0, _tourPoints.Count);

        _tourPoints.Insert(toIndex, moved);
        SaveTourPoints();
        RefreshPointsDialog();
    }

    private void ShowDropIndicator(TourPointRow row, bool above)
    {
        int gapIndex = above ? row.Index : row.Index + 1;
        for (int i = 0; i < _dropGaps.Count; i++)
        {
            Color c = _dropGaps[i].Color;
            c.A = i == gapIndex ? 1f : 0f;
            _dropGaps[i].Color = c;
        }
    }

    private void OnManualTouchDragStarted(int index)
    {
        if (_manualDragIndex != null || index < 0 || index >= _tourPoints.Count)
        {
            return;
        }

        _manualDragIndex = index;
        _manualDragTargetIndex = index;

        TourPointData point = _tourPoints[index];
        _manualDragPreview = new Label
        {
            Text = $"({point.PanX:F4}, {point.PanY:F4})  zoom {point.Zoom:F1}x  rot {point.Rotation:F0}°",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _pointsWindow.AddChild(_manualDragPreview);
    }

    private void OnManualTouchDragMoved(Vector2 position)
    {
        if (_manualDragIndex == null)
        {
            return;
        }

        if (_manualDragPreview != null)
        {
            _manualDragPreview.Position = position + new Vector2(16, 16);
        }

        TourPointRow nearestRow = null;
        bool above = true;

        foreach (Node child in _pointsListContainer.GetChildren())
        {
            if (child is not TourPointRow row)
            {
                continue;
            }

            nearestRow = row;
            Rect2 rect = row.GetGlobalRect();
            if (position.Y < rect.Position.Y + rect.Size.Y / 2.0f)
            {
                above = true;
                break;
            }
            above = false;
        }

        if (nearestRow != null)
        {
            ShowDropIndicator(nearestRow, above);
            _manualDragTargetIndex = above ? nearestRow.Index : nearestRow.Index + 1;
        }
    }

    private void OnManualTouchDragEnded(Vector2 position)
    {
        if (_manualDragIndex == null)
        {
            return;
        }

        OnManualTouchDragMoved(position);

        int fromIndex = _manualDragIndex.Value;
        int toIndex = _manualDragTargetIndex ?? fromIndex;

        _manualDragPreview?.QueueFree();
        _manualDragPreview = null;
        _manualDragIndex = null;
        _manualDragTargetIndex = null;
        ClearDropGaps();

        if (toIndex != fromIndex)
        {
            ReorderTourPoint(fromIndex, toIndex);
        }
    }

    private void DeleteTourPoint(int index)
    {
        if (index < 0 || index >= _tourPoints.Count)
        {
            return;
        }

        _tourPoints.RemoveAt(index);
        if (_tourIndex >= _tourPoints.Count)
        {
            _tourIndex = 0;
        }
        SaveTourPoints();
        RefreshPointsDialog();
    }

    private void BuildPointsDialog(Theme theme)
    {
        _pointsWindow = new DraggableWindow
        {
            Title = "Editing Tour",
            Theme = theme,
            Size = new Vector2I(500, 480),
            MinSize = new Vector2I(300, 240),
            Visible = false,
            Unresizable = false
        };
        _pointsWindow.CloseRequested += _pointsWindow.Hide;
        _pointsWindow.TouchDragMoved += OnManualTouchDragMoved;
        _pointsWindow.TouchDragEnded += OnManualTouchDragEnded;
        AddChild(_pointsWindow);

        // DraggableWindow builds its own background/border/title bar/close button/resize
        // handles in _Ready() (which just ran via AddChild above) - only the actual dialog
        // content is built here, into ContentArea. See DraggableWindow's usage notes.
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        _pointsWindow.ContentArea.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _pointsScrollContainer = scroll;
        vbox.AddChild(scroll);

        // Small gap between the row content (delete button on the right edge) and the
        // scrollbar, which otherwise sits flush against it.
        var listMargin = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        listMargin.AddThemeConstantOverride("margin_right", 10);
        scroll.AddChild(listMargin);

        _pointsListContainer = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        listMargin.AddChild(_pointsListContainer);

        var bottomBar = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        bottomBar.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(bottomBar);

        var resetButton = CreateIconButton("ResetButton", "reset", 180f);
        resetButton.Pressed += ResetTourPointsToDefaults;
        bottomBar.AddChild(resetButton);

        var closeButton = CreateIconButton("CloseButton", "close", 180f);
        closeButton.Pressed += _pointsWindow.Hide;
        bottomBar.AddChild(closeButton);
    }

    private ColorRect CreateDropGap()
    {
        var gap = new ColorRect
        {
            Color = new Color(0.97f, 0.8f, 0.2f, 0f),
            CustomMinimumSize = new Vector2(0, 6)
        };
        _dropGaps.Add(gap);
        return gap;
    }

    private void ClearDropGaps()
    {
        foreach (ColorRect gap in _dropGaps)
        {
            Color c = gap.Color;
            if (c.A != 0f)
            {
                c.A = 0f;
                gap.Color = c;
            }
        }
    }

    private bool IsPointsScrollbarVisible()
    {
        return _pointsScrollContainer != null && _pointsScrollContainer.GetVScrollBar().Visible;
    }

    // Drives the ScrollContainer directly from a right-half row drag. A fractional remainder
    // is accumulated across calls since ScrollVertical is an int - truncating each individual
    // delta on its own would throw away sub-pixel motion every event and make scrolling feel
    // much slower than the actual finger movement.
    private void OnManualScrollDelta(float deltaY)
    {
        if (_pointsScrollContainer == null)
        {
            return;
        }

        _pointsScrollRemainder += deltaY;
        int wholePixels = (int)_pointsScrollRemainder;
        _pointsScrollRemainder -= wholePixels;
        _pointsScrollContainer.ScrollVertical -= wholePixels;
    }

    private void RefreshPointsDialog()
    {
        // Defensive reset in case the dialog was closed/reopened mid drag, which would
        // otherwise leave stale state that permanently blocks OnManualTouchDragStarted's
        // re-entry guard.
        _manualDragPreview?.QueueFree();
        _manualDragPreview = null;
        _manualDragIndex = null;
        _manualDragTargetIndex = null;

        foreach (Node child in _pointsListContainer.GetChildren())
        {
            child.QueueFree();
        }

        _dropGaps.Clear();

        // A fixed-size gap sits before every row (plus one trailing gap after the last), all
        // normally transparent. Highlighting one as a drop hint only toggles its color, never
        // its size, so rows never reflow/bounce while dragging.
        _pointsListContainer.AddChild(CreateDropGap());

        for (int i = 0; i < _tourPoints.Count; i++)
        {
            int index = i; // captured per-row for the button closures below
            TourPointData point = _tourPoints[i];
            string pointText = $"({point.PanX:F4}, {point.PanY:F4})  zoom {point.Zoom:F1}x  rot {point.Rotation:F0}°";

            var row = new TourPointRow
            {
                Index = index,
                PreviewText = pointText,
                ShowDropIndicator = ShowDropIndicator,
                ReorderRequested = ReorderTourPoint,
                ManualTouchDragStarted = OnManualTouchDragStarted,
                IsScrollbarVisible = IsPointsScrollbarVisible,
                ManualScrollDelta = OnManualScrollDelta
            };

            var label = new Label
            {
                Text = pointText,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            row.AddChild(label);

            var goButton = new Button { Text = "▶" };
            goButton.Pressed += () => GoToTourPoint(index);
            row.AddChild(goButton);

            var deleteButton = new Button { Text = "✕" };
            deleteButton.Pressed += () => DeleteTourPoint(index);
            row.AddChild(deleteButton);

            _pointsListContainer.AddChild(row);
            _pointsListContainer.AddChild(CreateDropGap());
        }
    }

    private void SetRotationValue(float degrees)
    {
        _rotation = degrees;
        UpdateShaderUniforms();
    }

    private void UpdateShaderUniforms()
    {
        _mandelbrotShader.SetShaderParameter("pan_offset", new Vector2((float)_panOffset.X, (float)_panOffset.Y));
        _mandelbrotShader.SetShaderParameter("zoom", (float)_zoom);
        _mandelbrotShader.SetShaderParameter("rotation", Mathf.DegToRad(_rotation));
    }

    private void TakeScreenshot()
    {
        Image image = GetViewport().GetTexture().GetImage();
        string path = ProjectSettings.GlobalizePath("res://docs/snapshot.png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        image.SavePng(path);
    }

    // Writes the current tour points as a CreateDefaultTourPoints()-shaped code block, so it
    // can be copy-pasted back into source to promote the current list to the new defaults.
    private void ExportTourPointsAsCode()
    {
        var sb = new StringBuilder();
        sb.AppendLine("private static List<TourPointData> CreateDefaultTourPoints() => new()");
        sb.AppendLine("{");
        foreach (TourPointData point in _tourPoints)
        {
            sb.AppendLine($"\tnew TourPointData {{ PanX = {FormatDouble(point.PanX)}, PanY = {FormatDouble(point.PanY)}, Zoom = {FormatDouble(point.Zoom)}, Rotation = {FormatRotation(point.Rotation)} }},");
        }
        sb.AppendLine("};");

        string path = ProjectSettings.GlobalizePath("res://tour.data");
        System.IO.File.WriteAllText(path, sb.ToString());
    }

    private static string FormatDouble(double value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatRotation(float value)
    {
        string s = value.ToString(CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E'))
        {
            s += ".0";
        }
        return s + "f";
    }

    private void OnQuitPressed()
    {
        GetTree().Quit();
    }
}
