using Godot;
using System;
// A Window is its own Viewport, and Godot only delivers _Input to nodes belonging to
// whichever viewport currently has focus - Main (in the main game viewport) never sees key
// events while this dialog has focus. This subclass catches Escape within its own viewport.
public partial class EscapeCatchingWindow : Window
{
	public event Action EscapePressed;

	// Window-scoped (not per-Control), so these keep firing for the whole duration of a
	// touch drag even after the finger has moved off whichever row originally started it -
	// unlike Control-level _gui_input, which stops reaching the original row once the
	// pointer is over a different one.
	public event Action<Vector2> TouchDragMoved;
	public event Action<Vector2> TouchDragEnded;

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo && keyEvent.Keycode == Key.Escape)
		{
			EscapePressed?.Invoke();
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
}
