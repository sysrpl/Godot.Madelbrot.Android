using Godot;
using System;
// One row in the tour points list. Drag-and-drop reordering needs _GetDragData/_CanDropData/
// _DropData, which are virtual methods only reachable via a proper Control subclass.
public partial class TourPointRow : HBoxContainer
{
	public int Index;
	public string PreviewText;
	public Action<TourPointRow, bool> ShowDropIndicator;
	public Action<int, int> ReorderRequested;

	// Reports "a touch drag started on me" so Main can run a fully manual, engine-drag-free
	// touch reorder path (see Main.cs). Godot's built-in Control drag-and-drop
	// (_GetDragData/ForceDrag/_CanDropData/_DropData below) is fundamentally mouse-oriented -
	// its "drag is over" detection doesn't reliably fire from a touch release when
	// pointing/emulate_mouse_from_touch is off, which left the viewport stuck thinking a
	// drag was permanently active and blocked every subsequent drag attempt. The mouse path
	// below is left untouched since it already works correctly on desktop.
	public Action<int> ManualTouchDragStarted;
	// On Android, whether the tour points list currently has a visible vertical scrollbar
	// (i.e. is actually scrollable). Only checked there - see _GuiInput below.
	public Func<bool> IsScrollbarVisible;
	// Reports the drag's vertical delta so Main can scroll the list manually. The row fully
	// captures its own touch input (that's exactly why dragging anywhere used to trigger a
	// reorder instead of scrolling in the first place), so simply declining to start a reorder
	// does not let the event bubble to the ScrollContainer on its own - it has to be driven
	// explicitly, the same way the reorder drag itself is manual.
	public Action<float> ManualScrollDelta;

	private bool _manualTouchDragReported;
	private bool _reorderAllowedForCurrentTouch = true;

	public override Variant _GetDragData(Vector2 atPosition)
	{
		SetDragPreview(new Label { Text = PreviewText });
		return Index;
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventScreenTouch touchDown && touchDown.Pressed)
		{
			// Once the list is scrollable, only the left half of a row starts a reorder drag;
			// the right half instead drives the ScrollContainer manually (see ManualScrollDelta).
			bool restrictToLeftHalf = Main.IsAndroid && (IsScrollbarVisible?.Invoke() ?? false);
			_reorderAllowedForCurrentTouch = !restrictToLeftHalf || touchDown.Position.X < Size.X / 2.0f;
		}
		else if (@event is InputEventScreenDrag dragEvent)
		{
			if (!_reorderAllowedForCurrentTouch)
			{
				ManualScrollDelta?.Invoke(dragEvent.Relative.Y);
				return;
			}

			if (!_manualTouchDragReported)
			{
				_manualTouchDragReported = true;
				ManualTouchDragStarted?.Invoke(Index);
			}
		}
		else if (@event is InputEventScreenTouch touch && !touch.Pressed)
		{
			_manualTouchDragReported = false;
		}
	}

	public override bool _CanDropData(Vector2 atPosition, Variant data)
	{
		if (data.VariantType != Variant.Type.Int)
		{
			return false;
		}

		ShowDropIndicator?.Invoke(this, atPosition.Y < Size.Y / 2.0f);
		return true;
	}

	public override void _DropData(Vector2 atPosition, Variant data)
	{
		int fromIndex = data.AsInt32();
		int toIndex = atPosition.Y < Size.Y / 2.0f ? Index : Index + 1;
		ReorderRequested?.Invoke(fromIndex, toIndex);
	}
}
