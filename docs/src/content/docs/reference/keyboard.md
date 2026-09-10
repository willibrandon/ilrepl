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
| Escape | Dismiss the palette. |
| Home, End, Left, Right | Move within the line; with Ctrl, by word. |
| Shift+arrows | Select in the buffer. Typing replaces the selection and Ctrl+C copies it. |
| Ctrl+U | Delete from the caret back to the start of the line; at the start of a line, join it to the line above. |
| Ctrl+Z, Ctrl+Y | Undo and redo. |
| Ctrl+L | Clear the transcript. |
| Shift+Up | On an empty prompt, select the last transcript line. Shift+Up and Shift+Down extend the selection, y copies it, and Escape or a click ends it. Dragging with the mouse selects as well. |
| Ctrl+C | Copy the selection; otherwise clear the buffer, or cancel a block that is going by; otherwise quit. |
| Ctrl+Q | Quit. |

In the browser, selection and copy are the terminal's own: drag to select and press y, Cmd+C, or
Ctrl+C to copy. Shift+Up and Ctrl+C's copy of a buffer selection belong to the desktop.

The palette opens on an opcode or command prefix, then follows the operand being edited. A type
name leads to its members after `::`; field instructions offer fields, `ldloc` and `ldarg` offer
slots, and branches offer labels in the same body. Type declarations and `.dis` have completion
too. The engine reads the earlier lines of the unsent buffer to determine what is in scope.
After a bare `call ` or another empty type position, Tab opens the type list.

Operand matching ignores case and recognizes the capitals of a name: `wr` and `WL` both find
`WriteLine`. The inserted spelling uses the correct case, qualification and quotes. Tab, Enter
after moving in the palette, and a click accept the highlighted row as one undoable edit.
The grey suffix appears only when that edit appends to the text already there. The pane under
the rows shows the selected signature in full; PageUp and PageDown reveal the rest when it wraps.

A block goes to the engine line by line once Enter sends it. A paste lands in the editor and
waits for Enter. A line the engine refuses brings its whole block back with that line selected;
a line on its own is not put back, and Up recalls it.
Every submission is one history entry; see [Editing blocks](/usage/editing/).
