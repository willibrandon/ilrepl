---
title: Keyboard
description: Keys in the terminal UI.
---

| Key | Action |
| --- | --- |
| Enter | Submit the line. |
| Tab | Accept the highlighted completion. |
| Up, Down | Move through the completion palette when it is open; otherwise walk history. |
| Right | Accept the inline prediction of the best match. |
| Escape | Dismiss the palette. |
| Home, End, Left, Right | Move within the line. |
| Ctrl+L | Clear the transcript. |
| Ctrl+Q | Quit. |
| Ctrl+C | Quit. |

The palette opens while the first word of the line is a prefix of an opcode or, when it starts
with a dot, of a command. Each row shows the name, its stack transition, and a short description.
