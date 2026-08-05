using System;
using Godot;

// ============================================================================
// DraggableWindow
// ============================================================================
//
// THE PROBLEM THIS CLASS WORKS AROUND
// ------------------------------------
// Godot's Window node, when embedded (i.e. Project Settings > Display > Window
// > Subwindows > Embed Subwindows is enabled, which is the default, and is
// what makes a Window render inside the main viewport instead of as a
// separate OS window), draws its border/caption bar/background using two
// theme StyleBox items: "embedded_border" and "embedded_unfocused_border".
//
// In theory you restyle a window by overriding those two items:
//
//     var style = new StyleBoxFlat { BgColor = ... };
//     someWindow.AddThemeStyleboxOverride("embedded_border", style);
//     someWindow.AddThemeStyleboxOverride("embedded_unfocused_border", style);
//
// In practice this is a long-standing, well-known Godot quirk: instead of
// swapping in your custom look, the border, caption area, and background
// frequently just disappear - the window renders as fully transparent except
// for whatever child Controls you placed inside it. This reproduces even
// though the override itself is correctly registered (verified directly
// against the engine's theme system) - the *rendering* of it is what's
// broken/unreliable, not the API.
//
// One specific contributing trap, discovered while chasing this: the height
// of the caption strip is NOT controlled by the StyleBox's BorderWidthTop.
// It's a wholly separate theme constant, "title_height" (default 36). Trying
// to fake a thick caption bar by inflating BorderWidthTop produces a
// degenerate draw rect and blanks the frame out further. Setting
// "title_height" correctly helps but does not reliably fix the underlying
// transparency bug on its own.
//
// THE FIX USED HERE
// ------------------
// Rather than continuing to fight Window's native chrome rendering, this
// class sidesteps it entirely:
//
//   1. Sets Borderless = true, so Godot never attempts to draw its own
//      (buggy) embedded_border/caption/background at all.
//   2. Builds the entire visible frame itself out of ordinary Control/Panel
//      nodes with standard StyleBoxFlat theming - background, border,
//      rounded corners, a caption bar, a centered title Label, and a close
//      button. Regular Control theming is NOT subject to the Window chrome
//      bug, so this renders reliably.
//
// SIDE EFFECTS OF Borderless = true (AND HOW THIS CLASS COMPENSATES)
// ---------------------------------------------------------------------
// Turning off Godot's native window chrome also turns off the interactions
// that chrome used to provide for free. This class re-implements them:
//
//   - Dragging by the caption bar: the caption Control listens for GuiInput
//     (mouse button down/up + motion, or single-finger touch/drag - Android
//     doesn't synthesize mouse events from touch in this project, see
//     project.godot's pointing/emulate_mouse_from_touch=false) and moves the
//     window's Position directly. See OnTitleBarGuiInput.
//
//   - Resizing from the window edges/corner: five thin, invisible Control
//     "handles" (left, right, top, bottom, and a bottom-right corner) are
//     layered on top of the frame, each with the appropriate resize cursor.
//     Dragging one (mouse or touch) adjusts Size, and for the left/top edges
//     also shifts Position, so the opposite edge of the window stays
//     anchored in place while resizing. See OnResizeHandleGuiInput.
//
// USAGE NOTES
// -----------
//   - Add your dialog's content to `ContentArea`, NOT directly to the window
//     via AddChild. ContentArea is the Control that sits below the caption
//     bar; adding children straight to the window would just stack them
//     behind/in front of the frame and caption Controls instead of inside
//     the reserved content region.
//
//   - `ContentArea` is only populated once the window has entered the scene
//     tree and its _Ready() has run (that's where all of this is built). Call
//     AddChild() on the window BEFORE touching window.ContentArea:
//
//         var win = new DraggableWindow { Title = "Example" };
//         someParent.AddChild(win);           // _Ready() runs here
//         win.ContentArea.AddChild(myControl); // now safe
//         win.PopupCentered();
//
//   - This only matters for embedded subwindows (see above). If your project
//     runs with Embed Subwindows OFF, Window nodes become real OS windows and
//     none of this styling machinery applies (the OS draws its own chrome).
//
//   - Colors/fonts come from the assigned Theme, not hardcoded values, via three
//     theme type names this class looks up directly (Theme.GetStylebox/GetColor/
//     GetFontSize) and applies as per-instance overrides: "WindowTitleBar" (a
//     Panel style - the caption strip background), "WindowTitleLabel" (a Label
//     style - the caption text), and "WindowCloseButton" (a Button style - the
//     "X" button). These are looked up by exact type-name string rather than
//     via Control.ThemeTypeVariation - the latter did not reliably resolve for
//     an embedded Window's descendants (same class of unreliable Theme
//     propagation as embedded_border, see above), so the assigned Theme is
//     required to actually define these three type names (see minimal.tres).
//     The window body/frame just uses the ordinary Panel style.
//
//   - The caption height (32px), minimum size (200x150), and resize edge
//     thickness (6px) below are hardcoded for this demo project. Treat them
//     as a starting point - pull them out into constructor parameters or
//     public properties if you need this class to look different across
//     multiple windows in a real project.
// ============================================================================
public partial class DraggableWindow : Window
{
    // Children of the dialog (your actual UI) should be added here, not
    // directly to the window - see the usage notes above.
    public Control ContentArea { get; private set; }

    // Window-scoped (not per-Control) touch drag passthrough, for callers that need to run a
    // manual, engine-drag-free touch reorder/drag path (see TourPointRow/Main in this project).
    // A Window is its own Viewport, and Godot only delivers _Input to nodes belonging to
    // whichever viewport currently has focus, so this can't be caught from outside the window -
    // it has to be caught here, in the window's own _Input. These keep firing for the whole
    // duration of a drag even after the finger has moved off whichever control started it -
    // unlike Control-level _GuiInput, which stops reaching the originating control once the
    // pointer is over a different one.
    public event Action<Vector2> TouchDragMoved;
    public event Action<Vector2> TouchDragEnded;

    // Which side(s) of the window a given resize handle affects. The
    // bottom-right corner handle sets both Right and Bottom at once.
    [Flags]
    private enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private static readonly Vector2I DefaultMinWindowSize = new Vector2I(200, 150);

    private Control _titleBar;
    private bool _dragging;
    private Vector2 _dragAnchor;
    private ResizeEdge _resizeEdges = ResizeEdge.None;
    private Vector2 _resizeAnchor;

    public override void _Ready()
    {
        // Skip Godot's native window chrome entirely - see the file header
        // for why. Unresizable = false is the default already, but it's set
        // explicitly here since resizing is meaningless without it and this
        // class's whole point is to make resizing work again.
        Borderless = true;
        Unresizable = false;

        const int titleHeight = 32;

        // 1. Background panel: plain Panel, no override - picks up the assigned Theme's
        // ordinary Panel style (background/border/corner radius), same as every other panel
        // in the app. Ordinary Control theming is immune to the Window chrome bug described
        // above, unlike Window's own embedded_border.
        var frame = new Panel();
        frame.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        frame.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(frame);

        // 2. Caption strip. This Control is also what makes the window draggable - see
        // OnTitleBarGuiInput.
        _titleBar = new Control();
        _titleBar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _titleBar.OffsetBottom = titleHeight;
        _titleBar.MouseFilter = Control.MouseFilterEnum.Stop;
        _titleBar.GuiInput += OnTitleBarGuiInput;

        var titlePanel = new Panel
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        titlePanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        // Theme Type Variations (ThemeTypeVariation = "WindowTitleBar") turned out not to
        // resolve reliably for an embedded Window's descendants - same class of unreliable
        // Theme propagation as embedded_border, see the file header. Looking the named entries
        // up directly from the assigned Theme and applying them as per-instance overrides is
        // the same mechanism already used everywhere else in this project, so it's known-good.
        titlePanel.AddThemeStyleboxOverride("panel", Theme.GetStylebox("panel", "WindowTitleBar"));
        _titleBar.AddChild(titlePanel);

        var titleLabel = new Label
        {
            Text = Title,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        titleLabel.OffsetLeft = 28; // keep the text centered within the space left of the close button
        titleLabel.OffsetRight = -28;
        titleLabel.AddThemeColorOverride("font_color", Theme.GetColor("font_color", "WindowTitleLabel"));
        titleLabel.AddThemeFontSizeOverride("font_size", Theme.GetFontSize("font_size", "WindowTitleLabel"));
        _titleBar.AddChild(titleLabel);

        var closeButton = new Button
        {
            Text = "✕",
            Flat = true,
            FocusMode = Control.FocusModeEnum.None
        };
        closeButton.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        closeButton.OffsetLeft = -28;
        closeButton.OffsetTop = 4;
        closeButton.OffsetRight = -4;
        closeButton.OffsetBottom = titleHeight - 4;
        closeButton.AddThemeStyleboxOverride("normal", Theme.GetStylebox("normal", "WindowCloseButton"));
        closeButton.AddThemeStyleboxOverride("hover", Theme.GetStylebox("hover", "WindowCloseButton"));
        closeButton.AddThemeStyleboxOverride("pressed", Theme.GetStylebox("pressed", "WindowCloseButton"));
        closeButton.AddThemeStyleboxOverride("focus", Theme.GetStylebox("focus", "WindowCloseButton"));
        closeButton.AddThemeStyleboxOverride("disabled", Theme.GetStylebox("disabled", "WindowCloseButton"));
        closeButton.AddThemeColorOverride("font_color", Theme.GetColor("font_color", "WindowCloseButton"));
        closeButton.AddThemeColorOverride("font_hover_color", Theme.GetColor("font_hover_color", "WindowCloseButton"));
        closeButton.AddThemeFontSizeOverride("font_size", Theme.GetFontSize("font_size", "WindowCloseButton"));
        // FontVariation + VariationEmbolden is Godot's built-in "faux bold" - it synthesizes a
        // bolder weight from the existing font instead of requiring a separate bold font
        // resource. Left as a runtime override rather than a Theme entry since it composes
        // with ThemeDB's fallback font object, which isn't a static resource a .tres can name.
        closeButton.AddThemeFontOverride("font", new FontVariation
        {
            BaseFont = ThemeDB.FallbackFont,
            VariationEmbolden = 1.2f
        });
        // Emits the standard Window.CloseRequested signal rather than freeing the window
        // outright, so callers that want to reuse the same instance (show/hide across repeated
        // opens) can just do `window.CloseRequested += window.Hide;`, exactly as they would for
        // a native window's OS close button.
        closeButton.Pressed += () => EmitSignal(SignalName.CloseRequested);
        // Button's own click detection only reacts to InputEventMouseButton, so on Android
        // (project.godot: pointing/emulate_mouse_from_touch=false, so touch never synthesizes
        // one) Pressed above never fires from a tap. Supplemented here with a direct
        // touch-release check; MouseFilter=Stop on this Control (Button's default) already
        // keeps these touches from also reaching the title bar's own drag handling below.
        closeButton.GuiInput += @event =>
        {
            if (@event is InputEventScreenTouch touch && !touch.Pressed)
            {
                EmitSignal(SignalName.CloseRequested);
            }
        };
        _titleBar.AddChild(closeButton);

        AddChild(_titleBar);

        // 3. Everything below the caption strip is fair game for real
        // content. Callers should add their UI to ContentArea, not to the
        // window directly (see usage notes in the file header).
        ContentArea = new Control();
        ContentArea.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        ContentArea.OffsetTop = titleHeight;
        AddChild(ContentArea);

        // 4. Borderless windows also lose the engine's built-in edge/corner
        // resize handles, so drive resizing manually from thin, invisible
        // strips along each edge (plus one for the bottom-right corner).
        const int edgeThickness = 6;

        var leftEdge = CreateResizeHandle(Control.LayoutPreset.LeftWide, ResizeEdge.Left, Control.CursorShape.Hsize);
        leftEdge.OffsetRight = edgeThickness;
        AddChild(leftEdge);

        var rightEdge = CreateResizeHandle(Control.LayoutPreset.RightWide, ResizeEdge.Right, Control.CursorShape.Hsize);
        rightEdge.OffsetLeft = -edgeThickness;
        AddChild(rightEdge);

        var topEdge = CreateResizeHandle(Control.LayoutPreset.TopWide, ResizeEdge.Top, Control.CursorShape.Vsize);
        topEdge.OffsetBottom = edgeThickness;
        AddChild(topEdge);

        var bottomEdge = CreateResizeHandle(Control.LayoutPreset.BottomWide, ResizeEdge.Bottom, Control.CursorShape.Vsize);
        bottomEdge.OffsetTop = -edgeThickness;
        AddChild(bottomEdge);

        // The corner handle is added last so it renders on top of (and takes
        // input priority over) the tail ends of the edge handles it overlaps.
        var corner = CreateResizeHandle(Control.LayoutPreset.BottomRight, ResizeEdge.Right | ResizeEdge.Bottom, Control.CursorShape.Fdiagsize);
        corner.OffsetLeft = -edgeThickness * 2;
        corner.OffsetTop = -edgeThickness * 2;
        AddChild(corner);
    }

    // A Window is its own Viewport, so Godot only delivers _Input to nodes belonging to
    // whichever viewport currently has focus - a node outside this window never sees these
    // events while it has focus. Caught here instead, scoped to the window's own viewport.
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo && keyEvent.Keycode == Key.Escape)
        {
            EmitSignal(SignalName.CloseRequested);
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventScreenDrag dragEvent)
        {
            TouchDragMoved?.Invoke(dragEvent.Position);
        }
        else if (@event is InputEventScreenTouch touchEvent && !touchEvent.Pressed)
        {
            TouchDragEnded?.Invoke(touchEvent.Position);
        }
    }

    private Control CreateResizeHandle(Control.LayoutPreset preset, ResizeEdge edges, Control.CursorShape cursor)
    {
        var handle = new Control();
        handle.SetAnchorsPreset(preset);
        handle.MouseDefaultCursorShape = cursor;
        handle.MouseFilter = Control.MouseFilterEnum.Stop;
        handle.GuiInput += @event => OnResizeHandleGuiInput(@event, edges);
        return handle;
    }

    // Manual replacement for the native "drag by title bar" behavior that Borderless = true
    // disables. Tracks the pointer position across frames and moves the window by the delta
    // each time it changes. Handles both mouse and single-finger touch/drag - see the file
    // header's note on pointing/emulate_mouse_from_touch for why touch needs its own branch.
    private void OnTitleBarGuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
                _dragging = mb.Pressed;
                _dragAnchor = GetTree().Root.GetMousePosition();
                break;
            case InputEventMouseMotion when _dragging:
                Vector2 mousePos = GetTree().Root.GetMousePosition();
                Position += (Vector2I)(mousePos - _dragAnchor);
                _dragAnchor = mousePos;
                break;
            case InputEventScreenTouch touch:
                _dragging = touch.Pressed;
                _dragAnchor = ToRootPosition(touch.Position);
                break;
            case InputEventScreenDrag drag when _dragging:
                Vector2 touchPos = ToRootPosition(drag.Position);
                Position += (Vector2I)(touchPos - _dragAnchor);
                _dragAnchor = touchPos;
                break;
        }
    }

    // InputEventScreenTouch/Drag positions are local to this embedded window's own viewport,
    // unlike GetTree().Root.GetMousePosition() (used for the mouse path above), which is
    // already in the root/main viewport's space that Position and Size are expressed in.
    // Converting here rather than accumulating raw local deltas matters specifically because
    // this same handler moves the window mid-drag: once Position shifts, a later event's local
    // coordinate shifts by the same amount even if the finger hasn't moved, which would make
    // the window appear to stop following the finger after the first step.
    private Vector2 ToRootPosition(Vector2 localPosition) => localPosition + (Vector2)Position;

    // Manual replacement for the native edge/corner resize handles that Borderless = true
    // disables. `edges` says which side(s) this particular handle controls. Handles both
    // mouse and single-finger touch/drag, same reasoning as OnTitleBarGuiInput above.
    private void OnResizeHandleGuiInput(InputEvent @event, ResizeEdge edges)
    {
        switch (@event)
        {
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
                _resizeEdges = mb.Pressed ? edges : ResizeEdge.None;
                _resizeAnchor = GetTree().Root.GetMousePosition();
                break;
            case InputEventMouseMotion when _resizeEdges != ResizeEdge.None:
                ApplyResize(GetTree().Root.GetMousePosition());
                break;
            case InputEventScreenTouch touch:
                _resizeEdges = touch.Pressed ? edges : ResizeEdge.None;
                _resizeAnchor = ToRootPosition(touch.Position);
                break;
            case InputEventScreenDrag drag when _resizeEdges != ResizeEdge.None:
                ApplyResize(ToRootPosition(drag.Position));
                break;
        }
    }

    // Shared by the mouse and touch branches of OnResizeHandleGuiInput. For the Left/Top
    // edges, growing/shrinking the window also has to move Position by the same amount so the
    // opposite edge (right/bottom) stays fixed in place, matching normal OS window-resize
    // behavior.
    private void ApplyResize(Vector2 pointerPos)
    {
        // Respects the standard Window.MinSize property (when the caller has set one), rather
        // than a value private to this class, so a caller that sets MinSize gets the resize
        // handles to actually honor it.
        Vector2I minSize = MinSize != Vector2I.Zero ? MinSize : DefaultMinWindowSize;

        Vector2I delta = (Vector2I)(pointerPos - _resizeAnchor);
        _resizeAnchor = pointerPos;

        Vector2I size = Size;
        Vector2I pos = Position;

        if (_resizeEdges.HasFlag(ResizeEdge.Right))
            size.X = Mathf.Max(size.X + delta.X, minSize.X);
        if (_resizeEdges.HasFlag(ResizeEdge.Bottom))
            size.Y = Mathf.Max(size.Y + delta.Y, minSize.Y);
        if (_resizeEdges.HasFlag(ResizeEdge.Left))
        {
            // Clamp width first, then derive the position shift from the actual (possibly
            // clamped) size change - not the raw pointer delta - so the window doesn't keep
            // drifting once it hits minSize.
            int newWidth = Mathf.Max(size.X - delta.X, minSize.X);
            pos.X += size.X - newWidth;
            size.X = newWidth;
        }
        if (_resizeEdges.HasFlag(ResizeEdge.Top))
        {
            int newHeight = Mathf.Max(size.Y - delta.Y, minSize.Y);
            pos.Y += size.Y - newHeight;
            size.Y = newHeight;
        }

        Size = size;
        Position = pos;
    }
}
