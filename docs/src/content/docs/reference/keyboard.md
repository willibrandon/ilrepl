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
| Right | Accept the inline prediction of the best match. |
| Escape | Dismiss the palette. |
| Home, End, Left, Right | Move within the line; with Ctrl, by word. |
| Shift+arrows | Select in the buffer. Typing replaces the selection and Ctrl+C copies it. |
| Ctrl+U | Delete from the caret back to the start of the line; at the start of a line, join it to the line above. |
| Ctrl+Z, Ctrl+Y | Undo and redo. |
| Ctrl+L | Clear the transcript. |
| Shift+Up | On an empty prompt, select the last transcript line. Shift+Up and Shift+Down extend the selection, y copies it, and Escape or a click ends it. Dragging with the mouse selects as well. |
| Ctrl+C | Copy the selection; otherwise clear the buffer, or cancel a block that is going by; otherwise quit. |
| Ctrl+Q | Quit. |

The palette opens while the first word of the line is a prefix of an opcode or, when it starts
with a dot, of a command. Each row shows the name, its stack transition, and a short description.

A block goes to the engine line by line once Enter sends it. A paste lands in the editor and
waits for Enter. A line the engine refuses brings its whole block back with that line selected;
a line on its own is not put back, and Up recalls it.
Every submission is one history entry; see [Editing blocks](/usage/editing/).
