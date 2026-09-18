---
title: Keyboard
description: Keys in the terminal UI.
---

| Key | Action |
| --- | --- |
| Enter | Send the buffer when its braces balance; continue it when a brace or a `/*` is open. Accept the highlighted completion after moving to it. |
| Tab | Accept the highlighted completion, or indent a blank continuation line. |
| Up, Down | Move through the completion palette when it is open; otherwise through the buffer's lines, and from its first or last line through history. |
| Ctrl+P, Ctrl+N | Walk history from any line of the buffer. |
| Right | Accept the grey suffix when it would append exactly as shown; otherwise move the caret. |
| PageUp, PageDown | Scroll the palette's detail pane. |
| F1 | Open instruction or diagnostic help; press F1 or Escape to return to the editor. |
| F8, Shift+F8 | Move to the next or previous diagnostic in the buffer. |
| Escape | Dismiss the palette or cancel a session dialog. |
| Home, End, Left, Right | Move within the line; with Ctrl, by word. |
| Shift+arrows | Select in the buffer. Typing replaces the selection and Ctrl+C copies it. |
| Ctrl+U | Delete from the caret back to the start of the line; at the start of a line, join it to the line above. |
| Ctrl+Z, Ctrl+Y | Undo and redo. |
| Ctrl+L | Clear the transcript. |
| Shift+Up | On an empty prompt, select the last transcript line. Shift+Up and Shift+Down extend the selection, y copies it, and Escape or a click ends it. Dragging with the mouse selects as well. |
| Ctrl+C | Copy a selection, interrupt running work, or clear idle input. An empty prompt stays open. |
| Ctrl+S | Save the session; ask for a path on the first save. |
| Ctrl+O | Choose a session to open without execution. |
| Alt+R | Retry process supervision when a recovery notice offers it; keep the current runtime. |
| Ctrl+Q | Quit; offer Save, Discard, and Cancel for a modified file-associated session. |

In the Save changes dialog, Up and Down or Shift+Tab and Tab move between Save, Discard, and Cancel.
Navigation wraps at either end. Enter confirms the focused choice; Escape cancels.

While work is running, the first Ctrl+C requests cancellation and keeps the runtime.
Package restores and builds show `Cancelling` while they stop. If user IL cannot stop, a notice offers a runtime restart.
Only another Ctrl+C after that notice confirms the restart. An infinite loop needs this confirmation.
Repeated presses after work finishes do not quit the application.

A restart retains source, definitions, edits, history, and the draft, but discards objects and static field values.
You can also use `.session restart`. [Session replay](/usage/sessions/#run-and-recall) has its own immediate-stop behavior.

The palette opens on an opcode or command prefix, then follows the operand being edited. A type
name leads to its members after `::`; field instructions offer fields, `ldloc` and `ldarg` offer
slots, and branches offer labels in the same body. Type declarations and `.dis` have completion
too. The engine reads the earlier lines of the unsent buffer to determine what is in scope.
After a bare `call ` or another empty type position, Tab opens the type list.

Operand matching ignores case and recognizes the capitals of a name: `wr` and `WL` both find
`WriteLine`. The inserted spelling uses the correct case, qualification and quotes. Tab, Enter
after moving in the palette, and a click accept the highlighted row as one undoable edit.
The grey suffix appears only when that edit appends to the text already there. The pane under
the rows shows the selected signature, stack effect, and instruction behavior. PageUp and PageDown reveal the rest when it wraps.

In F1 help, PageUp and PageDown scroll, Tab and Shift+Tab select a source location or documentation
link, and Enter opens it. F8 and Shift+F8 move between diagnostics. Only locations in the current
buffer move the caret; accepted source and imported IL offsets remain visible for reference.
The documentation URL stays visible for copying when a terminal cannot open links.

A block goes to the engine line by line once Enter sends it. A paste lands in the editor and
waits for Enter. A refused block comes back with the offending line selected, which may be
earlier than the branch that exposed the error;
a line on its own is not put back, and Up recalls it.
Every submission is one history entry; see [Editing blocks](/usage/editing/).
