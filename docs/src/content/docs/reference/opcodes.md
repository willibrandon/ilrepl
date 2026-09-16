---
title: Opcodes
description: Every CIL opcode ilrepl accepts, with its stack transition and operand.
---

Find an instruction's stack effect, behavior, and operand below. F1 shows the same help in the terminal.
The stack column reads pops then pushes, using the abbreviations ILAsm uses: `i` for int32 or native int,
`i8` for int64, `r4` and `r8` for floats, `ref` for an object reference, `1` for any single value, and `…`
when the count depends on the operand.

| Opcode | Stack | Operand |
| --- | --- | --- |
| [`add`](#add) | `1 1    → 1` | none |
| [`add.ovf`](#add-ovf) | `1 1    → 1` | none |
| [`add.ovf.un`](#add-ovf-un) | `1 1    → 1` | none |
| [`and`](#and) | `1 1    → 1` | none |
| [`arglist`](#arglist) | `→ i` | none |
| [`beq`](#beq) | `1 1    →` | label |
| [`beq.s`](#beq-s) | `1 1    →` | label |
| [`bge`](#bge) | `1 1    →` | label |
| [`bge.s`](#bge-s) | `1 1    →` | label |
| [`bge.un`](#bge-un) | `1 1    →` | label |
| [`bge.un.s`](#bge-un-s) | `1 1    →` | label |
| [`bgt`](#bgt) | `1 1    →` | label |
| [`bgt.s`](#bgt-s) | `1 1    →` | label |
| [`bgt.un`](#bgt-un) | `1 1    →` | label |
| [`bgt.un.s`](#bgt-un-s) | `1 1    →` | label |
| [`ble`](#ble) | `1 1    →` | label |
| [`ble.s`](#ble-s) | `1 1    →` | label |
| [`ble.un`](#ble-un) | `1 1    →` | label |
| [`ble.un.s`](#ble-un-s) | `1 1    →` | label |
| [`blt`](#blt) | `1 1    →` | label |
| [`blt.s`](#blt-s) | `1 1    →` | label |
| [`blt.un`](#blt-un) | `1 1    →` | label |
| [`blt.un.s`](#blt-un-s) | `1 1    →` | label |
| [`bne.un`](#bne-un) | `1 1    →` | label |
| [`bne.un.s`](#bne-un-s) | `1 1    →` | label |
| [`box`](#box) | `1     → ref` | type |
| [`br`](#br) | `→` | label |
| [`br.s`](#br-s) | `→` | label |
| [`break`](#break) | `→` | none |
| [`brfalse`](#brfalse) | `i     →` | label |
| [`brfalse.s`](#brfalse-s) | `i     →` | label |
| [`brtrue`](#brtrue) | `i     →` | label |
| [`brtrue.s`](#brtrue-s) | `i     →` | label |
| [`call`](#call) | `…     → …` | method |
| [`calli`](#calli) | `…     → …` | signature |
| [`callvirt`](#callvirt) | `…     → …` | method |
| [`castclass`](#castclass) | `ref    → ref` | type |
| [`ceq`](#ceq) | `1 1    → i` | none |
| [`cgt`](#cgt) | `1 1    → i` | none |
| [`cgt.un`](#cgt-un) | `1 1    → i` | none |
| [`ckfinite`](#ckfinite) | `1     → r8` | none |
| [`clt`](#clt) | `1 1    → i` | none |
| [`clt.un`](#clt-un) | `1 1    → i` | none |
| [`constrained.`](#constrained) | `→` | type |
| [`conv.i`](#conv-i) | `1     → i` | none |
| [`conv.i1`](#conv-i1) | `1     → i` | none |
| [`conv.i2`](#conv-i2) | `1     → i` | none |
| [`conv.i4`](#conv-i4) | `1     → i` | none |
| [`conv.i8`](#conv-i8) | `1     → i8` | none |
| [`conv.ovf.i`](#conv-ovf-i) | `1     → i` | none |
| [`conv.ovf.i.un`](#conv-ovf-i-un) | `1     → i` | none |
| [`conv.ovf.i1`](#conv-ovf-i1) | `1     → i` | none |
| [`conv.ovf.i1.un`](#conv-ovf-i1-un) | `1     → i` | none |
| [`conv.ovf.i2`](#conv-ovf-i2) | `1     → i` | none |
| [`conv.ovf.i2.un`](#conv-ovf-i2-un) | `1     → i` | none |
| [`conv.ovf.i4`](#conv-ovf-i4) | `1     → i` | none |
| [`conv.ovf.i4.un`](#conv-ovf-i4-un) | `1     → i` | none |
| [`conv.ovf.i8`](#conv-ovf-i8) | `1     → i8` | none |
| [`conv.ovf.i8.un`](#conv-ovf-i8-un) | `1     → i8` | none |
| [`conv.ovf.u`](#conv-ovf-u) | `1     → i` | none |
| [`conv.ovf.u.un`](#conv-ovf-u-un) | `1     → i` | none |
| [`conv.ovf.u1`](#conv-ovf-u1) | `1     → i` | none |
| [`conv.ovf.u1.un`](#conv-ovf-u1-un) | `1     → i` | none |
| [`conv.ovf.u2`](#conv-ovf-u2) | `1     → i` | none |
| [`conv.ovf.u2.un`](#conv-ovf-u2-un) | `1     → i` | none |
| [`conv.ovf.u4`](#conv-ovf-u4) | `1     → i` | none |
| [`conv.ovf.u4.un`](#conv-ovf-u4-un) | `1     → i` | none |
| [`conv.ovf.u8`](#conv-ovf-u8) | `1     → i8` | none |
| [`conv.ovf.u8.un`](#conv-ovf-u8-un) | `1     → i8` | none |
| [`conv.r.un`](#conv-r-un) | `1     → r8` | none |
| [`conv.r4`](#conv-r4) | `1     → r4` | none |
| [`conv.r8`](#conv-r8) | `1     → r8` | none |
| [`conv.u`](#conv-u) | `1     → i` | none |
| [`conv.u1`](#conv-u1) | `1     → i` | none |
| [`conv.u2`](#conv-u2) | `1     → i` | none |
| [`conv.u4`](#conv-u4) | `1     → i` | none |
| [`conv.u8`](#conv-u8) | `1     → i8` | none |
| [`cpblk`](#cpblk) | `i i i   →` | none |
| [`cpobj`](#cpobj) | `i i    →` | type |
| [`div`](#div) | `1 1    → 1` | none |
| [`div.un`](#div-un) | `1 1    → 1` | none |
| [`dup`](#dup) | `1     → 1 1` | none |
| [`endfilter`](#endfilter) | `i     →` | none |
| [`endfinally`](#endfinally) | `→` | none |
| [`initblk`](#initblk) | `i i i   →` | none |
| [`initobj`](#initobj) | `i     →` | type |
| [`isinst`](#isinst) | `ref    → i` | type |
| [`jmp`](#jmp) | `→` | method |
| [`ldarg`](#ldarg) | `→ 1` | local or argument |
| [`ldarg.0`](#ldarg-0) | `→ 1` | none |
| [`ldarg.1`](#ldarg-1) | `→ 1` | none |
| [`ldarg.2`](#ldarg-2) | `→ 1` | none |
| [`ldarg.3`](#ldarg-3) | `→ 1` | none |
| [`ldarg.s`](#ldarg-s) | `→ 1` | local or argument |
| [`ldarga`](#ldarga) | `→ i` | local or argument |
| [`ldarga.s`](#ldarga-s) | `→ i` | local or argument |
| [`ldc.i4`](#ldc-i4) | `→ i` | int32 |
| [`ldc.i4.0`](#ldc-i4-0) | `→ i` | none |
| [`ldc.i4.1`](#ldc-i4-1) | `→ i` | none |
| [`ldc.i4.2`](#ldc-i4-2) | `→ i` | none |
| [`ldc.i4.3`](#ldc-i4-3) | `→ i` | none |
| [`ldc.i4.4`](#ldc-i4-4) | `→ i` | none |
| [`ldc.i4.5`](#ldc-i4-5) | `→ i` | none |
| [`ldc.i4.6`](#ldc-i4-6) | `→ i` | none |
| [`ldc.i4.7`](#ldc-i4-7) | `→ i` | none |
| [`ldc.i4.8`](#ldc-i4-8) | `→ i` | none |
| [`ldc.i4.m1`](#ldc-i4-m1) | `→ i` | none |
| [`ldc.i4.s`](#ldc-i4-s) | `→ i` | int8 |
| [`ldc.i8`](#ldc-i8) | `→ i8` | int64 |
| [`ldc.r4`](#ldc-r4) | `→ r4` | float32 |
| [`ldc.r8`](#ldc-r8) | `→ r8` | float64 |
| [`ldelem`](#ldelem) | `ref i   → 1` | type |
| [`ldelem.i`](#ldelem-i) | `ref i   → i` | none |
| [`ldelem.i1`](#ldelem-i1) | `ref i   → i` | none |
| [`ldelem.i2`](#ldelem-i2) | `ref i   → i` | none |
| [`ldelem.i4`](#ldelem-i4) | `ref i   → i` | none |
| [`ldelem.i8`](#ldelem-i8) | `ref i   → i8` | none |
| [`ldelem.r4`](#ldelem-r4) | `ref i   → r4` | none |
| [`ldelem.r8`](#ldelem-r8) | `ref i   → r8` | none |
| [`ldelem.ref`](#ldelem-ref) | `ref i   → ref` | none |
| [`ldelem.u1`](#ldelem-u1) | `ref i   → i` | none |
| [`ldelem.u2`](#ldelem-u2) | `ref i   → i` | none |
| [`ldelem.u4`](#ldelem-u4) | `ref i   → i` | none |
| [`ldelema`](#ldelema) | `ref i   → i` | type |
| [`ldfld`](#ldfld) | `ref    → 1` | field |
| [`ldflda`](#ldflda) | `ref    → i` | field |
| [`ldftn`](#ldftn) | `→ i` | method |
| [`ldind.i`](#ldind-i) | `i     → i` | none |
| [`ldind.i1`](#ldind-i1) | `i     → i` | none |
| [`ldind.i2`](#ldind-i2) | `i     → i` | none |
| [`ldind.i4`](#ldind-i4) | `i     → i` | none |
| [`ldind.i8`](#ldind-i8) | `i     → i8` | none |
| [`ldind.r4`](#ldind-r4) | `i     → r4` | none |
| [`ldind.r8`](#ldind-r8) | `i     → r8` | none |
| [`ldind.ref`](#ldind-ref) | `i     → ref` | none |
| [`ldind.u1`](#ldind-u1) | `i     → i` | none |
| [`ldind.u2`](#ldind-u2) | `i     → i` | none |
| [`ldind.u4`](#ldind-u4) | `i     → i` | none |
| [`ldlen`](#ldlen) | `ref    → i` | none |
| [`ldloc`](#ldloc) | `→ 1` | local or argument |
| [`ldloc.0`](#ldloc-0) | `→ 1` | none |
| [`ldloc.1`](#ldloc-1) | `→ 1` | none |
| [`ldloc.2`](#ldloc-2) | `→ 1` | none |
| [`ldloc.3`](#ldloc-3) | `→ 1` | none |
| [`ldloc.s`](#ldloc-s) | `→ 1` | local or argument |
| [`ldloca`](#ldloca) | `→ i` | local or argument |
| [`ldloca.s`](#ldloca-s) | `→ i` | local or argument |
| [`ldnull`](#ldnull) | `→ ref` | none |
| [`ldobj`](#ldobj) | `i     → 1` | type |
| [`ldsfld`](#ldsfld) | `→ 1` | field |
| [`ldsflda`](#ldsflda) | `→ i` | field |
| [`ldstr`](#ldstr) | `→ ref` | string |
| [`ldtoken`](#ldtoken) | `→ i` | token |
| [`ldvirtftn`](#ldvirtftn) | `ref    → i` | method |
| [`leave`](#leave) | `→` | label |
| [`leave.s`](#leave-s) | `→` | label |
| [`localloc`](#localloc) | `i     → i` | none |
| [`mkrefany`](#mkrefany) | `i     → 1` | type |
| [`mul`](#mul) | `1 1    → 1` | none |
| [`mul.ovf`](#mul-ovf) | `1 1    → 1` | none |
| [`mul.ovf.un`](#mul-ovf-un) | `1 1    → 1` | none |
| [`neg`](#neg) | `1     → 1` | none |
| [`newarr`](#newarr) | `i     → ref` | type |
| [`newobj`](#newobj) | `…     → ref` | method |
| [`no.`](#no) | `→` | mask |
| [`nop`](#nop) | `→` | none |
| [`not`](#not) | `1     → 1` | none |
| [`or`](#or) | `1 1    → 1` | none |
| [`pop`](#pop) | `1     →` | none |
| [`readonly.`](#readonly) | `→` | none |
| [`refanytype`](#refanytype) | `1     → i` | none |
| [`refanyval`](#refanyval) | `1     → i` | type |
| [`rem`](#rem) | `1 1    → 1` | none |
| [`rem.un`](#rem-un) | `1 1    → 1` | none |
| [`ret`](#ret) | `…     →` | none |
| [`rethrow`](#rethrow) | `→` | none |
| [`shl`](#shl) | `1 1    → 1` | none |
| [`shr`](#shr) | `1 1    → 1` | none |
| [`shr.un`](#shr-un) | `1 1    → 1` | none |
| [`sizeof`](#sizeof) | `→ i` | type |
| [`starg`](#starg) | `1     →` | local or argument |
| [`starg.s`](#starg-s) | `1     →` | local or argument |
| [`stelem`](#stelem) | `ref i 1  →` | type |
| [`stelem.i`](#stelem-i) | `ref i i  →` | none |
| [`stelem.i1`](#stelem-i1) | `ref i i  →` | none |
| [`stelem.i2`](#stelem-i2) | `ref i i  →` | none |
| [`stelem.i4`](#stelem-i4) | `ref i i  →` | none |
| [`stelem.i8`](#stelem-i8) | `ref i i8 →` | none |
| [`stelem.r4`](#stelem-r4) | `ref i r4 →` | none |
| [`stelem.r8`](#stelem-r8) | `ref i r8 →` | none |
| [`stelem.ref`](#stelem-ref) | `ref i ref →` | none |
| [`stfld`](#stfld) | `ref 1   →` | field |
| [`stind.i`](#stind-i) | `i i    →` | none |
| [`stind.i1`](#stind-i1) | `i i    →` | none |
| [`stind.i2`](#stind-i2) | `i i    →` | none |
| [`stind.i4`](#stind-i4) | `i i    →` | none |
| [`stind.i8`](#stind-i8) | `i i8   →` | none |
| [`stind.r4`](#stind-r4) | `i r4   →` | none |
| [`stind.r8`](#stind-r8) | `i r8   →` | none |
| [`stind.ref`](#stind-ref) | `i i    →` | none |
| [`stloc`](#stloc) | `1     →` | local or argument |
| [`stloc.0`](#stloc-0) | `1     →` | none |
| [`stloc.1`](#stloc-1) | `1     →` | none |
| [`stloc.2`](#stloc-2) | `1     →` | none |
| [`stloc.3`](#stloc-3) | `1     →` | none |
| [`stloc.s`](#stloc-s) | `1     →` | local or argument |
| [`stobj`](#stobj) | `i 1    →` | type |
| [`stsfld`](#stsfld) | `1     →` | field |
| [`sub`](#sub) | `1 1    → 1` | none |
| [`sub.ovf`](#sub-ovf) | `1 1    → 1` | none |
| [`sub.ovf.un`](#sub-ovf-un) | `1 1    → 1` | none |
| [`switch`](#switch) | `i     →` | labels |
| [`tail.`](#tail) | `→` | none |
| [`throw`](#throw) | `ref    →` | none |
| [`unaligned.`](#unaligned) | `→` | int8 |
| [`unbox`](#unbox) | `ref    → i` | type |
| [`unbox.any`](#unbox-any) | `ref    → 1` | type |
| [`volatile.`](#volatile) | `→` | none |
| [`xor`](#xor) | `1 1    → 1` | none |

<h2 id="add"><code>add</code></h2>

`add`

Stack: `1 1       → 1`

Adds the two operands. Integer overflow wraps; floating-point overflow produces infinity.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.add)

<h2 id="add-ovf"><code>add.ovf</code></h2>

`add.ovf`

Stack: `1 1       → 1`

Adds the two operands, throwing OverflowException if the integer result is out of range.

Operands and the result range are signed integers.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.add_ovf)

<h2 id="add-ovf-un"><code>add.ovf.un</code></h2>

`add.ovf.un`

Stack: `1 1       → 1`

Adds the two operands, throwing OverflowException if the integer result is out of range.

Operands and the result range are unsigned integers.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.add_ovf_un)

<h2 id="and"><code>and</code></h2>

`and`

Stack: `1 1       → 1`

Computes the bitwise AND of two integer operands.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.and)

<h2 id="arglist"><code>arglist</code></h2>

`arglist`

Stack: `→ i`

Pushes a handle to the current variable-argument list; the enclosing method must use vararg.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.arglist)

<h2 id="beq"><code>beq</code></h2>

`beq label`

Stack: `1 1       →`

Branches when the operand below the top is equal to the top operand; otherwise falls through.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.beq)

<h2 id="beq-s"><code>beq.s</code></h2>

`beq.s label`

Stack: `1 1       →`

Branches when the operand below the top is equal to the top operand; otherwise falls through.

The short form uses a smaller encoded operand; its operation is otherwise the same.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.beq_s)

<h2 id="bge"><code>bge</code></h2>

`bge label`

Stack: `1 1       →`

Branches when the operand below the top is greater than or equal to the top operand; otherwise falls through.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.bge)

<h2 id="bge-s"><code>bge.s</code></h2>

`bge.s label`

Stack: `1 1       →`

Branches when the operand below the top is greater than or equal to the top operand; otherwise falls through.

The short form uses a smaller encoded operand; its operation is otherwise the same.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.bge_s)

<h2 id="bge-un"><code>bge.un</code></h2>

`bge.un label`

Stack: `1 1       →`

Branches when the operand below the top is greater than or equal to the top operand; otherwise falls through.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.bge_un)

<h2 id="bge-un-s"><code>bge.un.s</code></h2>

`bge.un.s label`

Stack: `1 1       →`

Branches when the operand below the top is greater than or equal to the top operand; otherwise falls through.

The short form uses a smaller encoded operand; its operation is otherwise the same.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.bge_un_s)

<h2 id="bgt"><code>bgt</code></h2>

`bgt label`

Stack: `1 1       →`

Branches when the operand below the top is greater than the top operand; otherwise falls through.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.bgt)

<h2 id="bgt-s"><code>bgt.s</code></h2>

`bgt.s label`

Stack: `1 1       →`

Branches when the operand below the top is greater than the top operand; otherwise falls through.

The short form uses a smaller encoded operand; its operation is otherwise the same.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.bgt_s)

<h2 id="bgt-un"><code>bgt.un</code></h2>

`bgt.un label`

Stack: `1 1       →`

Branches when the operand below the top is greater than the top operand; otherwise falls through.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.bgt_un)

<h2 id="bgt-un-s"><code>bgt.un.s</code></h2>

`bgt.un.s label`

Stack: `1 1       →`

Branches when the operand below the top is greater than the top operand; otherwise falls through.

The short form uses a smaller encoded operand; its operation is otherwise the same.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.bgt_un_s)

<h2 id="ble"><code>ble</code></h2>

`ble label`

Stack: `1 1       →`

Branches when the operand below the top is less than or equal to the top operand; otherwise falls through.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ble)

<h2 id="ble-s"><code>ble.s</code></h2>

`ble.s label`

Stack: `1 1       →`

Branches when the operand below the top is less than or equal to the top operand; otherwise falls through.

The short form uses a smaller encoded operand; its operation is otherwise the same.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ble_s)

<h2 id="ble-un"><code>ble.un</code></h2>

`ble.un label`

Stack: `1 1       →`

Branches when the operand below the top is less than or equal to the top operand; otherwise falls through.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ble_un)

<h2 id="ble-un-s"><code>ble.un.s</code></h2>

`ble.un.s label`

Stack: `1 1       →`

Branches when the operand below the top is less than or equal to the top operand; otherwise falls through.

The short form uses a smaller encoded operand; its operation is otherwise the same.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ble_un_s)

<h2 id="blt"><code>blt</code></h2>

`blt label`

Stack: `1 1       →`

Branches when the operand below the top is less than the top operand; otherwise falls through.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.blt)

<h2 id="blt-s"><code>blt.s</code></h2>

`blt.s label`

Stack: `1 1       →`

Branches when the operand below the top is less than the top operand; otherwise falls through.

The short form uses a smaller encoded operand; its operation is otherwise the same.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.blt_s)

<h2 id="blt-un"><code>blt.un</code></h2>

`blt.un label`

Stack: `1 1       →`

Branches when the operand below the top is less than the top operand; otherwise falls through.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.blt_un)

<h2 id="blt-un-s"><code>blt.un.s</code></h2>

`blt.un.s label`

Stack: `1 1       →`

Branches when the operand below the top is less than the top operand; otherwise falls through.

The short form uses a smaller encoded operand; its operation is otherwise the same.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.blt_un_s)

<h2 id="bne-un"><code>bne.un</code></h2>

`bne.un label`

Stack: `1 1       →`

Branches when the operand below the top is not equal to the top operand; otherwise falls through.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.bne_un)

<h2 id="bne-un-s"><code>bne.un.s</code></h2>

`bne.un.s label`

Stack: `1 1       →`

Branches when the operand below the top is not equal to the top operand; otherwise falls through.

The short form uses a smaller encoded operand; its operation is otherwise the same.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.bne_un_s)

<h2 id="box"><code>box</code></h2>

`box type`

Stack: `1         → ref`

Converts a value to its boxed representation; non-nullable value types are copied into an object.

Nullable boxing produces null for no value, otherwise a boxed underlying value; generic behavior depends on the type.

A reference-type operand leaves the reference unchanged.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.box)

<h2 id="br"><code>br</code></h2>

`br label`

Stack: `→`

Transfers control to the target label without consuming stack values.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.br)

<h2 id="br-s"><code>br.s</code></h2>

`br.s label`

Stack: `→`

Transfers control to the target label without consuming stack values.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.br_s)

<h2 id="break"><code>break</code></h2>

`break`

Stack: `→`

Signals a breakpoint to the debugger; handling depends on the runtime and debugger.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.break)

<h2 id="brfalse"><code>brfalse</code></h2>

`brfalse label`

Stack: `i         →`

Pops a condition and branches when it is zero or a null reference or pointer.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.brfalse)

<h2 id="brfalse-s"><code>brfalse.s</code></h2>

`brfalse.s label`

Stack: `i         →`

Pops a condition and branches when it is zero or a null reference or pointer.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.brfalse_s)

<h2 id="brtrue"><code>brtrue</code></h2>

`brtrue label`

Stack: `i         →`

Pops a condition and branches when it is nonzero or a non-null reference or pointer.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.brtrue)

<h2 id="brtrue-s"><code>brtrue.s</code></h2>

`brtrue.s label`

Stack: `i         →`

Pops a condition and branches when it is nonzero or a non-null reference or pointer.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.brtrue_s)

<h2 id="call"><code>call</code></h2>

`call method`

Stack: `…         → …`

Invokes the method named by the operand directly, including an instance or virtual method's implementation.

Push the receiver first when required, then arguments in signature order; a void return pushes nothing.

An instance call still needs a receiver; call does not itself provide callvirt's null check.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.call)

<h2 id="calli"><code>calli</code></h2>

`calli signature`

Stack: `…         → …`

Calls the function pointer on top of the stack using the supplied calling convention and signature.

Push the receiver first when required, then arguments in signature order; a void return pushes nothing.

Push the function pointer after the receiver and arguments. This operation is unverifiable.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.calli)

<h2 id="callvirt"><code>callvirt</code></h2>

`callvirt method`

Stack: `…         → …`

Calls an instance method, using the receiver's runtime type for virtual or interface dispatch.

Push the receiver first when required, then arguments in signature order; a void return pushes nothing.

Nonvirtual instance methods are also allowed. An ordinary null object receiver throws NullReferenceException.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.callvirt)

<h2 id="castclass"><code>castclass</code></h2>

`castclass type`

Stack: `ref       → ref`

Checks an object reference against a type, throwing InvalidCastException when incompatible; null stays null.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.castclass)

<h2 id="ceq"><code>ceq</code></h2>

`ceq`

Stack: `1 1       → i`

Pushes int32 1 when the operand below the top is equal to the top operand; otherwise pushes int32 0.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ceq)

<h2 id="cgt"><code>cgt</code></h2>

`cgt`

Stack: `1 1       → i`

Pushes int32 1 when the operand below the top is greater than the top operand; otherwise pushes int32 0.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.cgt)

<h2 id="cgt-un"><code>cgt.un</code></h2>

`cgt.un`

Stack: `1 1       → i`

Pushes int32 1 when the operand below the top is greater than the top operand; otherwise pushes int32 0.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

Object references also support the non-null test against null.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.cgt_un)

<h2 id="ckfinite"><code>ckfinite</code></h2>

`ckfinite`

Stack: `1         → r8`

Checks a floating-point value and throws ArithmeticException for NaN or infinity.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ckfinite)

<h2 id="clt"><code>clt</code></h2>

`clt`

Stack: `1 1       → i`

Pushes int32 1 when the operand below the top is less than the top operand; otherwise pushes int32 0.

Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.clt)

<h2 id="clt-un"><code>clt.un</code></h2>

`clt.un`

Stack: `1 1       → i`

Pushes int32 1 when the operand below the top is less than the top operand; otherwise pushes int32 0.

Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.clt_un)

<h2 id="constrained"><code>constrained.</code></h2>

`constrained. type`

Stack: `→`

Adapts the following callvirt to a type, or resolves a static virtual interface call or ldftn.

For callvirt, the receiver is a managed pointer to the constrained type; boxing occurs only when required.

For call or ldftn, the target must be a static virtual interface method implemented by the constrained type.

Prefixes attach to the following instruction. Branches must target the first prefix, not the middle of the sequence.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.constrained)

<h2 id="conv-i"><code>conv.i</code></h2>

`conv.i`

Stack: `1         → i`

Converts the top value to native int without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_i)

<h2 id="conv-i1"><code>conv.i1</code></h2>

`conv.i1`

Stack: `1         → i`

Converts the top value to int8 without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_i1)

<h2 id="conv-i2"><code>conv.i2</code></h2>

`conv.i2`

Stack: `1         → i`

Converts the top value to int16 without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_i2)

<h2 id="conv-i4"><code>conv.i4</code></h2>

`conv.i4`

Stack: `1         → i`

Converts the top value to int32 without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_i4)

<h2 id="conv-i8"><code>conv.i8</code></h2>

`conv.i8`

Stack: `1         → i8`

Converts the top value to int64 without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_i8)

<h2 id="conv-ovf-i"><code>conv.ovf.i</code></h2>

`conv.ovf.i`

Stack: `1         → i`

Converts the top value to native int, throwing OverflowException if it cannot be represented.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_i)

<h2 id="conv-ovf-i-un"><code>conv.ovf.i.un</code></h2>

`conv.ovf.i.un`

Stack: `1         → i`

Converts the top value to native int, throwing OverflowException if it cannot be represented.

The source integer is interpreted as unsigned; the destination type is specified separately.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_i_un)

<h2 id="conv-ovf-i1"><code>conv.ovf.i1</code></h2>

`conv.ovf.i1`

Stack: `1         → i`

Converts the top value to int8, throwing OverflowException if it cannot be represented.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_i1)

<h2 id="conv-ovf-i1-un"><code>conv.ovf.i1.un</code></h2>

`conv.ovf.i1.un`

Stack: `1         → i`

Converts the top value to int8, throwing OverflowException if it cannot be represented.

The source integer is interpreted as unsigned; the destination type is specified separately.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_i1_un)

<h2 id="conv-ovf-i2"><code>conv.ovf.i2</code></h2>

`conv.ovf.i2`

Stack: `1         → i`

Converts the top value to int16, throwing OverflowException if it cannot be represented.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_i2)

<h2 id="conv-ovf-i2-un"><code>conv.ovf.i2.un</code></h2>

`conv.ovf.i2.un`

Stack: `1         → i`

Converts the top value to int16, throwing OverflowException if it cannot be represented.

The source integer is interpreted as unsigned; the destination type is specified separately.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_i2_un)

<h2 id="conv-ovf-i4"><code>conv.ovf.i4</code></h2>

`conv.ovf.i4`

Stack: `1         → i`

Converts the top value to int32, throwing OverflowException if it cannot be represented.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_i4)

<h2 id="conv-ovf-i4-un"><code>conv.ovf.i4.un</code></h2>

`conv.ovf.i4.un`

Stack: `1         → i`

Converts the top value to int32, throwing OverflowException if it cannot be represented.

The source integer is interpreted as unsigned; the destination type is specified separately.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_i4_un)

<h2 id="conv-ovf-i8"><code>conv.ovf.i8</code></h2>

`conv.ovf.i8`

Stack: `1         → i8`

Converts the top value to int64, throwing OverflowException if it cannot be represented.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_i8)

<h2 id="conv-ovf-i8-un"><code>conv.ovf.i8.un</code></h2>

`conv.ovf.i8.un`

Stack: `1         → i8`

Converts the top value to int64, throwing OverflowException if it cannot be represented.

The source integer is interpreted as unsigned; the destination type is specified separately.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_i8_un)

<h2 id="conv-ovf-u"><code>conv.ovf.u</code></h2>

`conv.ovf.u`

Stack: `1         → i`

Converts the top value to native uint, throwing OverflowException if it cannot be represented.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_u)

<h2 id="conv-ovf-u-un"><code>conv.ovf.u.un</code></h2>

`conv.ovf.u.un`

Stack: `1         → i`

Converts the top value to native uint, throwing OverflowException if it cannot be represented.

The source integer is interpreted as unsigned; the destination type is specified separately.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_u_un)

<h2 id="conv-ovf-u1"><code>conv.ovf.u1</code></h2>

`conv.ovf.u1`

Stack: `1         → i`

Converts the top value to uint8, throwing OverflowException if it cannot be represented.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_u1)

<h2 id="conv-ovf-u1-un"><code>conv.ovf.u1.un</code></h2>

`conv.ovf.u1.un`

Stack: `1         → i`

Converts the top value to uint8, throwing OverflowException if it cannot be represented.

The source integer is interpreted as unsigned; the destination type is specified separately.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_u1_un)

<h2 id="conv-ovf-u2"><code>conv.ovf.u2</code></h2>

`conv.ovf.u2`

Stack: `1         → i`

Converts the top value to uint16, throwing OverflowException if it cannot be represented.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_u2)

<h2 id="conv-ovf-u2-un"><code>conv.ovf.u2.un</code></h2>

`conv.ovf.u2.un`

Stack: `1         → i`

Converts the top value to uint16, throwing OverflowException if it cannot be represented.

The source integer is interpreted as unsigned; the destination type is specified separately.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_u2_un)

<h2 id="conv-ovf-u4"><code>conv.ovf.u4</code></h2>

`conv.ovf.u4`

Stack: `1         → i`

Converts the top value to uint32, throwing OverflowException if it cannot be represented.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_u4)

<h2 id="conv-ovf-u4-un"><code>conv.ovf.u4.un</code></h2>

`conv.ovf.u4.un`

Stack: `1         → i`

Converts the top value to uint32, throwing OverflowException if it cannot be represented.

The source integer is interpreted as unsigned; the destination type is specified separately.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_u4_un)

<h2 id="conv-ovf-u8"><code>conv.ovf.u8</code></h2>

`conv.ovf.u8`

Stack: `1         → i8`

Converts the top value to uint64, throwing OverflowException if it cannot be represented.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_u8)

<h2 id="conv-ovf-u8-un"><code>conv.ovf.u8.un</code></h2>

`conv.ovf.u8.un`

Stack: `1         → i8`

Converts the top value to uint64, throwing OverflowException if it cannot be represented.

The source integer is interpreted as unsigned; the destination type is specified separately.

Floating-point sources truncate toward zero; .un does not change their interpretation.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_ovf_u8_un)

<h2 id="conv-r-un"><code>conv.r.un</code></h2>

`conv.r.un`

Stack: `1         → r8`

Converts the top value to floating point without an overflow check.

The source integer is interpreted as unsigned; the destination type is specified separately.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_r_un)

<h2 id="conv-r4"><code>conv.r4</code></h2>

`conv.r4`

Stack: `1         → r4`

Converts the top value to float32 without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_r4)

<h2 id="conv-r8"><code>conv.r8</code></h2>

`conv.r8`

Stack: `1         → r8`

Converts the top value to float64 without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_r8)

<h2 id="conv-u"><code>conv.u</code></h2>

`conv.u`

Stack: `1         → i`

Converts the top value to native uint without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_u)

<h2 id="conv-u1"><code>conv.u1</code></h2>

`conv.u1`

Stack: `1         → i`

Converts the top value to uint8 without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_u1)

<h2 id="conv-u2"><code>conv.u2</code></h2>

`conv.u2`

Stack: `1         → i`

Converts the top value to uint16 without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_u2)

<h2 id="conv-u4"><code>conv.u4</code></h2>

`conv.u4`

Stack: `1         → i`

Converts the top value to uint32 without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_u4)

<h2 id="conv-u8"><code>conv.u8</code></h2>

`conv.u8`

Stack: `1         → i8`

Converts the top value to uint64 without an overflow check.

The destination type controls the result; integer sources are interpreted as signed for overflow checks.

Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.conv_u8)

<h2 id="cpblk"><code>cpblk</code></h2>

`cpblk`

Stack: `i i i     →`

Copies a byte count from the source address to the destination address; overlapping ranges are unspecified.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.cpblk)

<h2 id="cpobj"><code>cpobj</code></h2>

`cpobj type`

Stack: `i i       →`

Copies the specified type's value from the source pointer to the destination pointer below it.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.cpobj)

<h2 id="div"><code>div</code></h2>

`div`

Stack: `1 1       → 1`

Divides the operand below the top by the top operand; integer division truncates toward zero.

Integer division by zero throws DivideByZeroException.

Floating-point division follows IEEE floating-point rules.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.div)

<h2 id="div-un"><code>div.un</code></h2>

`div.un`

Stack: `1 1       → 1`

Divides the operand below the top by the top operand, interpreting both integers as unsigned.

Integer division by zero throws DivideByZeroException.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.div_un)

<h2 id="dup"><code>dup</code></h2>

`dup`

Stack: `1         → 1 1`

Duplicates the top stack value. Both copies retain the same original producer.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.dup)

<h2 id="endfilter"><code>endfilter</code></h2>

`endfilter`

Stack: `i         →`

Ends an exception filter; exactly one int32 result chooses rejection (0) or acceptance (1).

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.endfilter)

<h2 id="endfinally"><code>endfinally</code></h2>

`endfinally`

Stack: `→`

Ends a finally or fault handler and resumes the pending exception or leave operation.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.endfinally)

<h2 id="initblk"><code>initblk</code></h2>

`initblk`

Stack: `i i i     →`

Fills a byte count at the destination address using the low byte of the supplied integer value.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.initblk)

<h2 id="initobj"><code>initobj</code></h2>

`initobj type`

Stack: `i         →`

Initializes the addressed storage to the type's default value without calling a constructor.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.initobj)

<h2 id="isinst"><code>isinst</code></h2>

`isinst type`

Stack: `ref       → i`

Tests an object reference against a type, returning the reference on success or null on failure.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.isinst)

<h2 id="jmp"><code>jmp</code></h2>

`jmp method`

Stack: `→`

Transfers directly to a method with a compatible signature, forwarding the current arguments.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.jmp)

<h2 id="ldarg"><code>ldarg</code></h2>

`ldarg argument`

Stack: `→ 1`

Loads an argument onto the evaluation stack. In instance methods, argument 0 is this.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldarg)

<h2 id="ldarg-0"><code>ldarg.0</code></h2>

`ldarg.0`

Stack: `→ 1`

Loads an argument onto the evaluation stack. In instance methods, argument 0 is this.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldarg_0)

<h2 id="ldarg-1"><code>ldarg.1</code></h2>

`ldarg.1`

Stack: `→ 1`

Loads an argument onto the evaluation stack. In instance methods, argument 0 is this.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldarg_1)

<h2 id="ldarg-2"><code>ldarg.2</code></h2>

`ldarg.2`

Stack: `→ 1`

Loads an argument onto the evaluation stack. In instance methods, argument 0 is this.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldarg_2)

<h2 id="ldarg-3"><code>ldarg.3</code></h2>

`ldarg.3`

Stack: `→ 1`

Loads an argument onto the evaluation stack. In instance methods, argument 0 is this.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldarg_3)

<h2 id="ldarg-s"><code>ldarg.s</code></h2>

`ldarg.s argument`

Stack: `→ 1`

Loads an argument onto the evaluation stack. In instance methods, argument 0 is this.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldarg_s)

<h2 id="ldarga"><code>ldarga</code></h2>

`ldarga argument`

Stack: `→ i`

Pushes a managed pointer to an argument, allowing access to the argument's storage.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldarga)

<h2 id="ldarga-s"><code>ldarga.s</code></h2>

`ldarga.s argument`

Stack: `→ i`

Pushes a managed pointer to an argument, allowing access to the argument's storage.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldarga_s)

<h2 id="ldc-i4"><code>ldc.i4</code></h2>

`ldc.i4 value`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4)

<h2 id="ldc-i4-0"><code>ldc.i4.0</code></h2>

`ldc.i4.0`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_0)

<h2 id="ldc-i4-1"><code>ldc.i4.1</code></h2>

`ldc.i4.1`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_1)

<h2 id="ldc-i4-2"><code>ldc.i4.2</code></h2>

`ldc.i4.2`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_2)

<h2 id="ldc-i4-3"><code>ldc.i4.3</code></h2>

`ldc.i4.3`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_3)

<h2 id="ldc-i4-4"><code>ldc.i4.4</code></h2>

`ldc.i4.4`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_4)

<h2 id="ldc-i4-5"><code>ldc.i4.5</code></h2>

`ldc.i4.5`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_5)

<h2 id="ldc-i4-6"><code>ldc.i4.6</code></h2>

`ldc.i4.6`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_6)

<h2 id="ldc-i4-7"><code>ldc.i4.7</code></h2>

`ldc.i4.7`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_7)

<h2 id="ldc-i4-8"><code>ldc.i4.8</code></h2>

`ldc.i4.8`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_8)

<h2 id="ldc-i4-m1"><code>ldc.i4.m1</code></h2>

`ldc.i4.m1`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_m1)

<h2 id="ldc-i4-s"><code>ldc.i4.s</code></h2>

`ldc.i4.s value`

Stack: `→ i`

Pushes the encoded numeric constant onto the evaluation stack.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i4_s)

<h2 id="ldc-i8"><code>ldc.i8</code></h2>

`ldc.i8 value`

Stack: `→ i8`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_i8)

<h2 id="ldc-r4"><code>ldc.r4</code></h2>

`ldc.r4 value`

Stack: `→ r4`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_r4)

<h2 id="ldc-r8"><code>ldc.r8</code></h2>

`ldc.r8 value`

Stack: `→ r8`

Pushes the encoded numeric constant onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldc_r8)

<h2 id="ldelem"><code>ldelem</code></h2>

`ldelem type`

Stack: `ref i     → 1`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem)

<h2 id="ldelem-i"><code>ldelem.i</code></h2>

`ldelem.i`

Stack: `ref i     → i`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_i)

<h2 id="ldelem-i1"><code>ldelem.i1</code></h2>

`ldelem.i1`

Stack: `ref i     → i`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_i1)

<h2 id="ldelem-i2"><code>ldelem.i2</code></h2>

`ldelem.i2`

Stack: `ref i     → i`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_i2)

<h2 id="ldelem-i4"><code>ldelem.i4</code></h2>

`ldelem.i4`

Stack: `ref i     → i`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_i4)

<h2 id="ldelem-i8"><code>ldelem.i8</code></h2>

`ldelem.i8`

Stack: `ref i     → i8`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_i8)

<h2 id="ldelem-r4"><code>ldelem.r4</code></h2>

`ldelem.r4`

Stack: `ref i     → r4`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_r4)

<h2 id="ldelem-r8"><code>ldelem.r8</code></h2>

`ldelem.r8`

Stack: `ref i     → r8`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_r8)

<h2 id="ldelem-ref"><code>ldelem.ref</code></h2>

`ldelem.ref`

Stack: `ref i     → ref`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_ref)

<h2 id="ldelem-u1"><code>ldelem.u1</code></h2>

`ldelem.u1`

Stack: `ref i     → i`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_u1)

<h2 id="ldelem-u2"><code>ldelem.u2</code></h2>

`ldelem.u2`

Stack: `ref i     → i`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_u2)

<h2 id="ldelem-u4"><code>ldelem.u4</code></h2>

`ldelem.u4`

Stack: `ref i     → i`

Pops an array and index, then loads that element using the specified element type.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelem_u4)

<h2 id="ldelema"><code>ldelema</code></h2>

`ldelema type`

Stack: `ref i     → i`

Pops an array and index and pushes a managed pointer to that element.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldelema)

<h2 id="ldfld"><code>ldfld</code></h2>

`ldfld field`

Stack: `ref       → 1`

Loads the named instance field from the receiver on the stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldfld)

<h2 id="ldflda"><code>ldflda</code></h2>

`ldflda field`

Stack: `ref       → i`

Pushes a managed pointer to the named instance field's storage.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldflda)

<h2 id="ldftn"><code>ldftn</code></h2>

`ldftn method`

Stack: `→ i`

Pushes a native function pointer to the named method without invoking it.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldftn)

<h2 id="ldind-i"><code>ldind.i</code></h2>

`ldind.i`

Stack: `i         → i`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_i)

<h2 id="ldind-i1"><code>ldind.i1</code></h2>

`ldind.i1`

Stack: `i         → i`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_i1)

<h2 id="ldind-i2"><code>ldind.i2</code></h2>

`ldind.i2`

Stack: `i         → i`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_i2)

<h2 id="ldind-i4"><code>ldind.i4</code></h2>

`ldind.i4`

Stack: `i         → i`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_i4)

<h2 id="ldind-i8"><code>ldind.i8</code></h2>

`ldind.i8`

Stack: `i         → i8`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_i8)

<h2 id="ldind-r4"><code>ldind.r4</code></h2>

`ldind.r4`

Stack: `i         → r4`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_r4)

<h2 id="ldind-r8"><code>ldind.r8</code></h2>

`ldind.r8`

Stack: `i         → r8`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_r8)

<h2 id="ldind-ref"><code>ldind.ref</code></h2>

`ldind.ref`

Stack: `i         → ref`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_ref)

<h2 id="ldind-u1"><code>ldind.u1</code></h2>

`ldind.u1`

Stack: `i         → i`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_u1)

<h2 id="ldind-u2"><code>ldind.u2</code></h2>

`ldind.u2`

Stack: `i         → i`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_u2)

<h2 id="ldind-u4"><code>ldind.u4</code></h2>

`ldind.u4`

Stack: `i         → i`

Loads a value through a managed or unmanaged pointer using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldind_u4)

<h2 id="ldlen"><code>ldlen</code></h2>

`ldlen`

Stack: `ref       → i`

Pops an array reference and pushes its length as an unsigned native integer.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldlen)

<h2 id="ldloc"><code>ldloc</code></h2>

`ldloc local`

Stack: `→ 1`

Loads the specified local variable onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldloc)

<h2 id="ldloc-0"><code>ldloc.0</code></h2>

`ldloc.0`

Stack: `→ 1`

Loads the specified local variable onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldloc_0)

<h2 id="ldloc-1"><code>ldloc.1</code></h2>

`ldloc.1`

Stack: `→ 1`

Loads the specified local variable onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldloc_1)

<h2 id="ldloc-2"><code>ldloc.2</code></h2>

`ldloc.2`

Stack: `→ 1`

Loads the specified local variable onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldloc_2)

<h2 id="ldloc-3"><code>ldloc.3</code></h2>

`ldloc.3`

Stack: `→ 1`

Loads the specified local variable onto the evaluation stack.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldloc_3)

<h2 id="ldloc-s"><code>ldloc.s</code></h2>

`ldloc.s local`

Stack: `→ 1`

Loads the specified local variable onto the evaluation stack.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldloc_s)

<h2 id="ldloca"><code>ldloca</code></h2>

`ldloca local`

Stack: `→ i`

Pushes a managed pointer to a local variable's storage.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldloca)

<h2 id="ldloca-s"><code>ldloca.s</code></h2>

`ldloca.s local`

Stack: `→ i`

Pushes a managed pointer to a local variable's storage.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldloca_s)

<h2 id="ldnull"><code>ldnull</code></h2>

`ldnull`

Stack: `→ ref`

Pushes a null object reference, which can be assigned to a reference type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldnull)

<h2 id="ldobj"><code>ldobj</code></h2>

`ldobj type`

Stack: `i         → 1`

Loads a value of the specified type through a pointer.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldobj)

<h2 id="ldsfld"><code>ldsfld</code></h2>

`ldsfld field`

Stack: `→ 1`

Loads the named static field; no instance receiver is needed.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldsfld)

<h2 id="ldsflda"><code>ldsflda</code></h2>

`ldsflda field`

Stack: `→ i`

Pushes a managed pointer to the named static field's storage.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldsflda)

<h2 id="ldstr"><code>ldstr</code></h2>

`ldstr "text"`

Stack: `→ ref`

Pushes the string literal as an object reference.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldstr)

<h2 id="ldtoken"><code>ldtoken</code></h2>

`ldtoken type, method, or field`

Stack: `→ i`

Pushes a runtime type, method, or field handle for the metadata operand.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldtoken)

<h2 id="ldvirtftn"><code>ldvirtftn</code></h2>

`ldvirtftn method`

Stack: `ref       → i`

Pops a receiver and obtains the function pointer selected by virtual dispatch without invoking it.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ldvirtftn)

<h2 id="leave"><code>leave</code></h2>

`leave label`

Stack: `→`

Exits to the target label, empties the evaluation stack, and runs intervening finally handlers.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.leave)

<h2 id="leave-s"><code>leave.s</code></h2>

`leave.s label`

Stack: `→`

Exits to the target label, empties the evaluation stack, and runs intervening finally handlers.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.leave_s)

<h2 id="localloc"><code>localloc</code></h2>

`localloc`

Stack: `i         → i`

Allocates local memory in the current stack frame and pushes a native pointer to it.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.localloc)

<h2 id="mkrefany"><code>mkrefany</code></h2>

`mkrefany type`

Stack: `i         → 1`

Combines an address and a type token into a typed reference.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.mkrefany)

<h2 id="mul"><code>mul</code></h2>

`mul`

Stack: `1 1       → 1`

Multiplies the two operands. Integer overflow wraps; floating-point overflow produces infinity.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.mul)

<h2 id="mul-ovf"><code>mul.ovf</code></h2>

`mul.ovf`

Stack: `1 1       → 1`

Multiplies the two operands, throwing OverflowException if the integer result is out of range.

Operands and the result range are signed integers.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.mul_ovf)

<h2 id="mul-ovf-un"><code>mul.ovf.un</code></h2>

`mul.ovf.un`

Stack: `1 1       → 1`

Multiplies the two operands, throwing OverflowException if the integer result is out of range.

Operands and the result range are unsigned integers.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.mul_ovf_un)

<h2 id="neg"><code>neg</code></h2>

`neg`

Stack: `1         → 1`

Negates a numeric value without checking integer overflow.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.neg)

<h2 id="newarr"><code>newarr</code></h2>

`newarr type`

Stack: `i         → ref`

Pops an integer length and creates a zero-based, one-dimensional array of the specified element type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.newarr)

<h2 id="newobj"><code>newobj</code></h2>

`newobj method`

Stack: `…         → ref`

Pops the constructor arguments and creates an initialized object or value; no receiver is supplied.

Push constructor arguments in signature order. A constructor has a void signature, but newobj pushes the instance.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.newobj)

<h2 id="no"><code>no.</code></h2>

`no. mask`

Stack: `→`

Permits selected type, range, or null checks on the following instruction to be skipped; skipping is optional.

Mask bits 1, 2, and 4 select type, range, and null checks. Valid targets depend on the selected checks; unverifiable.

Prefixes attach to the following instruction. Branches must target the first prefix, not the middle of the sequence.

Type checks (1): castclass, unbox, ldelema, stelem, stelem.ref.

Range checks (2): ldelem.*, stelem.*, ldelema.

Null checks (4): those array operations, ldfld, stfld, callvirt, ldvirtftn.

Every selected check must be valid for the target instruction.

[CLI specification](https://ecma-international.org/publications-and-standards/standards/ecma-335/)

<h2 id="nop"><code>nop</code></h2>

`nop`

Stack: `→`

Does nothing and leaves the evaluation stack unchanged.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.nop)

<h2 id="not"><code>not</code></h2>

`not`

Stack: `1         → 1`

Complements every bit of an integer value.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.not)

<h2 id="or"><code>or</code></h2>

`or`

Stack: `1 1       → 1`

Computes the bitwise OR of two integer operands.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.or)

<h2 id="pop"><code>pop</code></h2>

`pop`

Stack: `1         →`

Discards the top stack value.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.pop)

<h2 id="readonly"><code>readonly.</code></h2>

`readonly.`

Stack: `→`

Makes ldelema or an array Address call return a managed pointer with controlled mutability.

Suppresses the exact array-element type check, permitting covariant array reads.

Indirect writes through the result are unverifiable; field stores and mutating receiver calls are permitted.

Prefixes attach to the following instruction. Branches must target the first prefix, not the middle of the sequence.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.readonly)

<h2 id="refanytype"><code>refanytype</code></h2>

`refanytype`

Stack: `1         → i`

Extracts the runtime type handle from a typed reference.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.refanytype)

<h2 id="refanyval"><code>refanyval</code></h2>

`refanyval type`

Stack: `1         → i`

Extracts an address from a typed reference, checking that its type matches the operand.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.refanyval)

<h2 id="rem"><code>rem</code></h2>

`rem`

Stack: `1 1       → 1`

Computes the remainder of the operand below the top divided by the top operand.

Integer division by zero throws DivideByZeroException.

Uses a quotient truncated toward zero; floating-point rem differs from Math.IEEERemainder.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.rem)

<h2 id="rem-un"><code>rem.un</code></h2>

`rem.un`

Stack: `1 1       → 1`

Computes the remainder using unsigned integer operands.

Integer division by zero throws DivideByZeroException.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.rem_un)

<h2 id="ret"><code>ret</code></h2>

`ret`

Stack: `…         →`

Returns from the current method. A non-void method requires exactly one assignable return value.

A top-level ilrepl cell may return zero or one value; managed pointers cannot escape the cell.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.ret)

<h2 id="rethrow"><code>rethrow</code></h2>

`rethrow`

Stack: `→`

Rethrows the active exception from within its catch handler, preserving the original exception context.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.rethrow)

<h2 id="shl"><code>shl</code></h2>

`shl`

Stack: `1 1       → 1`

Shifts the integer below the top to the left by the top value, filling low bits with zero.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.shl)

<h2 id="shr"><code>shr</code></h2>

`shr`

Stack: `1 1       → 1`

Shifts a signed integer right, copying the sign bit into the vacated high bits.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.shr)

<h2 id="shr-un"><code>shr.un</code></h2>

`shr.un`

Stack: `1 1       → 1`

Shifts an integer right logically, filling the vacated high bits with zero.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.shr_un)

<h2 id="sizeof"><code>sizeof</code></h2>

`sizeof type`

Stack: `→ i`

Pushes the size in bytes of the specified type as int32; it does not consume an instance.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.sizeof)

<h2 id="starg"><code>starg</code></h2>

`starg argument`

Stack: `1         →`

Pops a value into the specified argument's storage; its type must be assignable to that argument.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.starg)

<h2 id="starg-s"><code>starg.s</code></h2>

`starg.s argument`

Stack: `1         →`

Pops a value into the specified argument's storage; its type must be assignable to that argument.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.starg_s)

<h2 id="stelem"><code>stelem</code></h2>

`stelem type`

Stack: `ref i 1   →`

Pops an array, index, and value, then stores the value into that array element.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stelem)

<h2 id="stelem-i"><code>stelem.i</code></h2>

`stelem.i`

Stack: `ref i i   →`

Pops an array, index, and value, then stores the value into that array element.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stelem_i)

<h2 id="stelem-i1"><code>stelem.i1</code></h2>

`stelem.i1`

Stack: `ref i i   →`

Pops an array, index, and value, then stores the value into that array element.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stelem_i1)

<h2 id="stelem-i2"><code>stelem.i2</code></h2>

`stelem.i2`

Stack: `ref i i   →`

Pops an array, index, and value, then stores the value into that array element.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stelem_i2)

<h2 id="stelem-i4"><code>stelem.i4</code></h2>

`stelem.i4`

Stack: `ref i i   →`

Pops an array, index, and value, then stores the value into that array element.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stelem_i4)

<h2 id="stelem-i8"><code>stelem.i8</code></h2>

`stelem.i8`

Stack: `ref i i8  →`

Pops an array, index, and value, then stores the value into that array element.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stelem_i8)

<h2 id="stelem-r4"><code>stelem.r4</code></h2>

`stelem.r4`

Stack: `ref i r4  →`

Pops an array, index, and value, then stores the value into that array element.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stelem_r4)

<h2 id="stelem-r8"><code>stelem.r8</code></h2>

`stelem.r8`

Stack: `ref i r8  →`

Pops an array, index, and value, then stores the value into that array element.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stelem_r8)

<h2 id="stelem-ref"><code>stelem.ref</code></h2>

`stelem.ref`

Stack: `ref i ref →`

Pops an array, index, and value, then stores the value into that array element.

The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stelem_ref)

<h2 id="stfld"><code>stfld</code></h2>

`stfld field`

Stack: `ref 1     →`

Pops a receiver and value and stores the value into the named instance field.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stfld)

<h2 id="stind-i"><code>stind.i</code></h2>

`stind.i`

Stack: `i i       →`

Stores the top value through the pointer below it using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stind_i)

<h2 id="stind-i1"><code>stind.i1</code></h2>

`stind.i1`

Stack: `i i       →`

Stores the top value through the pointer below it using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stind_i1)

<h2 id="stind-i2"><code>stind.i2</code></h2>

`stind.i2`

Stack: `i i       →`

Stores the top value through the pointer below it using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stind_i2)

<h2 id="stind-i4"><code>stind.i4</code></h2>

`stind.i4`

Stack: `i i       →`

Stores the top value through the pointer below it using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stind_i4)

<h2 id="stind-i8"><code>stind.i8</code></h2>

`stind.i8`

Stack: `i i8      →`

Stores the top value through the pointer below it using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stind_i8)

<h2 id="stind-r4"><code>stind.r4</code></h2>

`stind.r4`

Stack: `i r4      →`

Stores the top value through the pointer below it using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stind_r4)

<h2 id="stind-r8"><code>stind.r8</code></h2>

`stind.r8`

Stack: `i r8      →`

Stores the top value through the pointer below it using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stind_r8)

<h2 id="stind-ref"><code>stind.ref</code></h2>

`stind.ref`

Stack: `i i       →`

Stores the top value through the pointer below it using the instruction's storage type.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stind_ref)

<h2 id="stloc"><code>stloc</code></h2>

`stloc local`

Stack: `1         →`

Pops a value into the specified local variable; its type must be assignable to that local.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stloc)

<h2 id="stloc-0"><code>stloc.0</code></h2>

`stloc.0`

Stack: `1         →`

Pops a value into the specified local variable; its type must be assignable to that local.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stloc_0)

<h2 id="stloc-1"><code>stloc.1</code></h2>

`stloc.1`

Stack: `1         →`

Pops a value into the specified local variable; its type must be assignable to that local.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stloc_1)

<h2 id="stloc-2"><code>stloc.2</code></h2>

`stloc.2`

Stack: `1         →`

Pops a value into the specified local variable; its type must be assignable to that local.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stloc_2)

<h2 id="stloc-3"><code>stloc.3</code></h2>

`stloc.3`

Stack: `1         →`

Pops a value into the specified local variable; its type must be assignable to that local.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stloc_3)

<h2 id="stloc-s"><code>stloc.s</code></h2>

`stloc.s local`

Stack: `1         →`

Pops a value into the specified local variable; its type must be assignable to that local.

The short form uses a smaller encoded operand; its operation is otherwise the same.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stloc_s)

<h2 id="stobj"><code>stobj</code></h2>

`stobj type`

Stack: `i 1       →`

Copies the top value of the specified type into the storage addressed below it.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stobj)

<h2 id="stsfld"><code>stsfld</code></h2>

`stsfld field`

Stack: `1         →`

Pops a value and stores it into the named static field.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.stsfld)

<h2 id="sub"><code>sub</code></h2>

`sub`

Stack: `1 1       → 1`

Subtracts the top operand from the one below it. Integer overflow wraps; floating-point overflow produces infinity.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.sub)

<h2 id="sub-ovf"><code>sub.ovf</code></h2>

`sub.ovf`

Stack: `1 1       → 1`

Subtracts the top operand from the one below it, throwing OverflowException if the integer result is out of range.

Operands and the result range are signed integers.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.sub_ovf)

<h2 id="sub-ovf-un"><code>sub.ovf.un</code></h2>

`sub.ovf.un`

Stack: `1 1       → 1`

Subtracts the top operand from the one below it, throwing OverflowException if the integer result is out of range.

Operands and the result range are unsigned integers.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.sub_ovf_un)

<h2 id="switch"><code>switch</code></h2>

`switch (label, ...)`

Stack: `i         →`

Pops an int32 index and branches to that zero-based entry; an out-of-range index falls through.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.switch)

<h2 id="tail"><code>tail.</code></h2>

`tail.`

Stack: `→`

Marks the following call as a tail call, allowing transfer without retaining the current frame.

Prefix call, callvirt, or calli; only its arguments may remain. Follow the call with ret outside protected regions.

Prefixes attach to the following instruction. Branches must target the first prefix, not the middle of the sequence.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.tailcall)

<h2 id="throw"><code>throw</code></h2>

`throw`

Stack: `ref       →`

Pops and throws an exception object, transferring control to exception handling.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.throw)

<h2 id="unaligned"><code>unaligned.</code></h2>

`unaligned. 1|2|4`

Stack: `→`

Specifies that the following memory access can rely only on the supplied alignment (1, 2, or 4 bytes).

Applies to indirect loads/stores, instance-field access, ldobj/stobj, cpblk, or initblk.

Prefixes attach to the following instruction. Branches must target the first prefix, not the middle of the sequence.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.unaligned)

<h2 id="unbox"><code>unbox</code></h2>

`unbox type`

Stack: `ref       → i`

Returns a managed pointer to an unboxed value, allowing access to its storage rather than copying it.

Unboxing checks the boxed type; it does not perform a numeric conversion. A mismatched type throws InvalidCastException.

The result has controlled mutability. Unboxing Nullable<T> can require newly manufactured nullable storage.

Null becomes an empty nullable value for Nullable<T>; a non-nullable value-type operand throws NullReferenceException.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.unbox)

<h2 id="unbox-any"><code>unbox.any</code></h2>

`unbox.any type`

Stack: `ref       → 1`

Extracts a boxed value; for a reference-type operand it performs the same check as castclass.

Nullable boxing produces null for no value, otherwise a boxed underlying value; generic behavior depends on the type.

Unboxing checks the boxed type; it does not perform a numeric conversion. A mismatched type throws InvalidCastException.

Null becomes an empty nullable value for Nullable<T>; a non-nullable value-type operand throws NullReferenceException.

A reference-type operand preserves null. With a generic operand, the actual type determines the operation.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.unbox_any)

<h2 id="volatile"><code>volatile.</code></h2>

`volatile.`

Stack: `→`

Gives the following memory read acquire semantics or memory write release semantics.

Applies to indirect loads/stores, instance-field access, ldobj/stobj, cpblk, or initblk.

Also applies to ldsfld/stsfld. It does not make an otherwise non-atomic access atomic or replace a lock.

Prefixes attach to the following instruction. Branches must target the first prefix, not the middle of the sequence.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.volatile)

<h2 id="xor"><code>xor</code></h2>

`xor`

Stack: `1 1       → 1`

Computes the bitwise exclusive OR of two integer operands.

[Microsoft reference](https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes.xor)
