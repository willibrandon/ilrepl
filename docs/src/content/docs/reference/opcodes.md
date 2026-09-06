---
title: Opcodes
description: Every CIL opcode ilrepl accepts, with its stack transition and operand.
---

This table is generated from the engine's opcode table by `scripts/Generate-OpcodeReference.cs`.
The stack column reads pops then pushes, using the abbreviations ILAsm uses: `i` for int32 or native int,
`i8` for int64, `r4` and `r8` for floats, `ref` for an object reference, `1` for any single value, and `…`
when the count depends on the operand.

| Opcode | Stack | Operand | Description |
| --- | --- | --- | --- |
| `add` | `1 1    → 1` | none | add |
| `add.ovf` | `1 1    → 1` | none | add (overflow check) |
| `add.ovf.un` | `1 1    → 1` | none | add unsigned (overflow check) |
| `and` | `1 1    → 1` | none | bitwise and |
| `arglist` | `→ i` | none | push argument list handle (vararg cells) |
| `beq` | `1 1    →` | label | branch if equal |
| `beq.s` | `1 1    →` | label | branch if equal |
| `bge` | `1 1    →` | label | branch if >= |
| `bge.s` | `1 1    →` | label | branch if >= |
| `bge.un` | `1 1    →` | label | branch if >= (unsigned or unordered) |
| `bge.un.s` | `1 1    →` | label | branch if >= (unsigned or unordered) |
| `bgt` | `1 1    →` | label | branch if > |
| `bgt.s` | `1 1    →` | label | branch if > |
| `bgt.un` | `1 1    →` | label | branch if > (unsigned or unordered) |
| `bgt.un.s` | `1 1    →` | label | branch if > (unsigned or unordered) |
| `ble` | `1 1    →` | label | branch if <= |
| `ble.s` | `1 1    →` | label | branch if <= |
| `ble.un` | `1 1    →` | label | branch if <= (unsigned or unordered) |
| `ble.un.s` | `1 1    →` | label | branch if <= (unsigned or unordered) |
| `blt` | `1 1    →` | label | branch if < |
| `blt.s` | `1 1    →` | label | branch if < |
| `blt.un` | `1 1    →` | label | branch if < (unsigned or unordered) |
| `blt.un.s` | `1 1    →` | label | branch if < (unsigned or unordered) |
| `bne.un` | `1 1    →` | label | branch if != (unordered) |
| `bne.un.s` | `1 1    →` | label | branch if != (unordered) |
| `box` | `1     → ref` | type | box value type |
| `br` | `→` | label | branch |
| `br.s` | `→` | label | branch (short) |
| `break` | `→` | none | breakpoint trap |
| `brfalse` | `i     →` | label | branch if false, null, or zero |
| `brfalse.s` | `i     →` | label | branch if false, null, or zero |
| `brtrue` | `i     →` | label | branch if true or non-null |
| `brtrue.s` | `i     →` | label | branch if true or non-null |
| `call` | `…     → …` | method | call method |
| `calli` | `…     → …` | signature | call through function pointer |
| `callvirt` | `…     → …` | method | call virtual method on object |
| `castclass` | `ref    → ref` | type | cast (throws on failure) |
| `ceq` | `1 1    → i` | none | compare equal, push int32 |
| `cgt` | `1 1    → i` | none | compare greater, push int32 |
| `cgt.un` | `1 1    → i` | none | compare greater (unsigned or unordered), push int32 |
| `ckfinite` | `1     → r8` | none | throw if NaN or infinity |
| `clt` | `1 1    → i` | none | compare less, push int32 |
| `clt.un` | `1 1    → i` | none | compare less (unsigned or unordered), push int32 |
| `constrained.` | `→` | type | prefix: constrained callvirt |
| `conv.i` | `1     → i` | none | convert to native int |
| `conv.i1` | `1     → i` | none | convert to int8 |
| `conv.i2` | `1     → i` | none | convert to int16 |
| `conv.i4` | `1     → i` | none | convert to int32 |
| `conv.i8` | `1     → i8` | none | convert to int64 |
| `conv.ovf.i` | `1     → i` | none | convert to native int (overflow check) |
| `conv.ovf.i.un` | `1     → i` | none | convert to native int (unsigned source, overflow check) |
| `conv.ovf.i1` | `1     → i` | none | convert to int8 (overflow check) |
| `conv.ovf.i1.un` | `1     → i` | none | convert to int8 (unsigned source, overflow check) |
| `conv.ovf.i2` | `1     → i` | none | convert to int16 (overflow check) |
| `conv.ovf.i2.un` | `1     → i` | none | convert to int16 (unsigned source, overflow check) |
| `conv.ovf.i4` | `1     → i` | none | convert to int32 (overflow check) |
| `conv.ovf.i4.un` | `1     → i` | none | convert to int32 (unsigned source, overflow check) |
| `conv.ovf.i8` | `1     → i8` | none | convert to int64 (overflow check) |
| `conv.ovf.i8.un` | `1     → i8` | none | convert to int64 (unsigned source, overflow check) |
| `conv.ovf.u` | `1     → i` | none | convert to native uint (overflow check) |
| `conv.ovf.u.un` | `1     → i` | none | convert to native uint (unsigned source, overflow check) |
| `conv.ovf.u1` | `1     → i` | none | convert to uint8 (overflow check) |
| `conv.ovf.u1.un` | `1     → i` | none | convert to uint8 (unsigned source, overflow check) |
| `conv.ovf.u2` | `1     → i` | none | convert to uint16 (overflow check) |
| `conv.ovf.u2.un` | `1     → i` | none | convert to uint16 (unsigned source, overflow check) |
| `conv.ovf.u4` | `1     → i` | none | convert to uint32 (overflow check) |
| `conv.ovf.u4.un` | `1     → i` | none | convert to uint32 (unsigned source, overflow check) |
| `conv.ovf.u8` | `1     → i8` | none | convert to uint64 (overflow check) |
| `conv.ovf.u8.un` | `1     → i8` | none | convert to uint64 (unsigned source, overflow check) |
| `conv.r.un` | `1     → r8` | none | convert unsigned integer to float |
| `conv.r4` | `1     → r4` | none | convert to float32 |
| `conv.r8` | `1     → r8` | none | convert to float64 |
| `conv.u` | `1     → i` | none | convert to native uint |
| `conv.u1` | `1     → i` | none | convert to uint8 |
| `conv.u2` | `1     → i` | none | convert to uint16 |
| `conv.u4` | `1     → i` | none | convert to uint32 |
| `conv.u8` | `1     → i8` | none | convert to uint64 |
| `cpblk` | `i i i   →` | none | copy memory block |
| `cpobj` | `i i    →` | type | copy value type |
| `div` | `1 1    → 1` | none | divide |
| `div.un` | `1 1    → 1` | none | divide unsigned |
| `dup` | `1     → 1 1` | none | duplicate top of stack |
| `endfilter` | `i     →` | none | end exception filter |
| `endfinally` | `→` | none | end finally or fault handler |
| `initblk` | `i i i   →` | none | fill memory block |
| `initobj` | `i     →` | type | zero-initialize value type at address |
| `isinst` | `ref    → i` | type | type test (null when it fails) |
| `jmp` | `→` | method | jump to method (tail transfer) |
| `ldarg` | `→ 1` | local or argument | push argument |
| `ldarg.0` | `→ 1` | none | push argument 0 |
| `ldarg.1` | `→ 1` | none | push argument 1 |
| `ldarg.2` | `→ 1` | none | push argument 2 |
| `ldarg.3` | `→ 1` | none | push argument 3 |
| `ldarg.s` | `→ 1` | local or argument | push argument (byte index) |
| `ldarga` | `→ i` | local or argument | push argument address |
| `ldarga.s` | `→ i` | local or argument | push address of argument |
| `ldc.i4` | `→ i` | int32 | push int32 immediate |
| `ldc.i4.0` | `→ i` | none | push int32 0 |
| `ldc.i4.1` | `→ i` | none | push int32 1 |
| `ldc.i4.2` | `→ i` | none | push int32 2 |
| `ldc.i4.3` | `→ i` | none | push int32 3 |
| `ldc.i4.4` | `→ i` | none | push int32 4 |
| `ldc.i4.5` | `→ i` | none | push int32 5 |
| `ldc.i4.6` | `→ i` | none | push int32 6 |
| `ldc.i4.7` | `→ i` | none | push int32 7 |
| `ldc.i4.8` | `→ i` | none | push int32 8 |
| `ldc.i4.m1` | `→ i` | none | push int32 -1 |
| `ldc.i4.s` | `→ i` | int8 | push int32 (int8 immediate) |
| `ldc.i8` | `→ i8` | int64 | push int64 immediate |
| `ldc.r4` | `→ r4` | float32 | push float32 immediate |
| `ldc.r8` | `→ r8` | float64 | push float64 immediate |
| `ldelem` | `ref i   → 1` | type | load element of type |
| `ldelem.i` | `ref i   → i` | none | load native int element |
| `ldelem.i1` | `ref i   → i` | none | load int8 element |
| `ldelem.i2` | `ref i   → i` | none | load int16 element |
| `ldelem.i4` | `ref i   → i` | none | load int32 element |
| `ldelem.i8` | `ref i   → i8` | none | load int64 element |
| `ldelem.r4` | `ref i   → r4` | none | load float32 element |
| `ldelem.r8` | `ref i   → r8` | none | load float64 element |
| `ldelem.ref` | `ref i   → ref` | none | load object element |
| `ldelem.u1` | `ref i   → i` | none | load uint8 element |
| `ldelem.u2` | `ref i   → i` | none | load uint16 element |
| `ldelem.u4` | `ref i   → i` | none | load uint32 element |
| `ldelema` | `ref i   → i` | type | push element address |
| `ldfld` | `ref    → 1` | field | load instance field |
| `ldflda` | `ref    → i` | field | load instance field address |
| `ldftn` | `→ i` | method | push method pointer |
| `ldind.i` | `i     → i` | none | load native int through pointer |
| `ldind.i1` | `i     → i` | none | load int8 through pointer |
| `ldind.i2` | `i     → i` | none | load int16 through pointer |
| `ldind.i4` | `i     → i` | none | load int32 through pointer |
| `ldind.i8` | `i     → i8` | none | load int64 through pointer |
| `ldind.r4` | `i     → r4` | none | load float32 through pointer |
| `ldind.r8` | `i     → r8` | none | load float64 through pointer |
| `ldind.ref` | `i     → ref` | none | load object reference through pointer |
| `ldind.u1` | `i     → i` | none | load uint8 through pointer |
| `ldind.u2` | `i     → i` | none | load uint16 through pointer |
| `ldind.u4` | `i     → i` | none | load uint32 through pointer |
| `ldlen` | `ref    → i` | none | push array length |
| `ldloc` | `→ 1` | local or argument | push local |
| `ldloc.0` | `→ 1` | none | push local 0 |
| `ldloc.1` | `→ 1` | none | push local 1 |
| `ldloc.2` | `→ 1` | none | push local 2 |
| `ldloc.3` | `→ 1` | none | push local 3 |
| `ldloc.s` | `→ 1` | local or argument | push local (byte index) |
| `ldloca` | `→ i` | local or argument | push local address |
| `ldloca.s` | `→ i` | local or argument | push address of local |
| `ldnull` | `→ ref` | none | push null reference |
| `ldobj` | `i     → 1` | type | load value type through pointer |
| `ldsfld` | `→ 1` | field | load static field |
| `ldsflda` | `→ i` | field | load static field address |
| `ldstr` | `→ ref` | string | push string literal |
| `ldtoken` | `→ i` | token | push runtime handle |
| `ldvirtftn` | `ref    → i` | method | push virtual method pointer |
| `leave` | `→` | label | exit protected region |
| `leave.s` | `→` | label | exit protected region (short) |
| `localloc` | `i     → i` | none | allocate stack memory |
| `mkrefany` | `i     → 1` | type | make typed reference |
| `mul` | `1 1    → 1` | none | multiply |
| `mul.ovf` | `1 1    → 1` | none | multiply (overflow check) |
| `mul.ovf.un` | `1 1    → 1` | none | multiply unsigned (overflow check) |
| `neg` | `1     → 1` | none | negate |
| `newarr` | `i     → ref` | type | allocate array |
| `newobj` | `…     → ref` | method | allocate object and call constructor |
| `nop` | `→` | none | do nothing |
| `not` | `1     → 1` | none | bitwise complement |
| `or` | `1 1    → 1` | none | bitwise or |
| `pop` | `1     →` | none | discard top of stack |
| `readonly.` | `→` | none | prefix: readonly ldelema |
| `refanytype` | `1     → i` | none | typed reference to type handle |
| `refanyval` | `1     → i` | type | typed reference to address |
| `rem` | `1 1    → 1` | none | remainder |
| `rem.un` | `1 1    → 1` | none | remainder unsigned |
| `ret` | `…     →` | none | return |
| `rethrow` | `→` | none | rethrow current exception |
| `shl` | `1 1    → 1` | none | shift left |
| `shr` | `1 1    → 1` | none | shift right (arithmetic) |
| `shr.un` | `1 1    → 1` | none | shift right (logical) |
| `sizeof` | `→ i` | type | push size of type |
| `starg` | `1     →` | local or argument | pop into argument |
| `starg.s` | `1     →` | local or argument | pop into argument |
| `stelem` | `ref i 1  →` | type | store element of type |
| `stelem.i` | `ref i i  →` | none | store native int element |
| `stelem.i1` | `ref i i  →` | none | store int8 element |
| `stelem.i2` | `ref i i  →` | none | store int16 element |
| `stelem.i4` | `ref i i  →` | none | store int32 element |
| `stelem.i8` | `ref i i8 →` | none | store int64 element |
| `stelem.r4` | `ref i r4 →` | none | store float32 element |
| `stelem.r8` | `ref i r8 →` | none | store float64 element |
| `stelem.ref` | `ref i ref →` | none | store object element |
| `stfld` | `ref 1   →` | field | store instance field |
| `stind.i` | `i i    →` | none | store native int through pointer |
| `stind.i1` | `i i    →` | none | store int8 through pointer |
| `stind.i2` | `i i    →` | none | store int16 through pointer |
| `stind.i4` | `i i    →` | none | store int32 through pointer |
| `stind.i8` | `i i8   →` | none | store int64 through pointer |
| `stind.r4` | `i r4   →` | none | store float32 through pointer |
| `stind.r8` | `i r8   →` | none | store float64 through pointer |
| `stind.ref` | `i i    →` | none | store object reference through pointer |
| `stloc` | `1     →` | local or argument | pop into local |
| `stloc.0` | `1     →` | none | pop into local 0 |
| `stloc.1` | `1     →` | none | pop into local 1 |
| `stloc.2` | `1     →` | none | pop into local 2 |
| `stloc.3` | `1     →` | none | pop into local 3 |
| `stloc.s` | `1     →` | local or argument | pop into local |
| `stobj` | `i 1    →` | type | store value type through pointer |
| `stsfld` | `1     →` | field | store static field |
| `sub` | `1 1    → 1` | none | subtract |
| `sub.ovf` | `1 1    → 1` | none | subtract (overflow check) |
| `sub.ovf.un` | `1 1    → 1` | none | subtract unsigned (overflow check) |
| `switch` | `i     →` | labels | jump table on int32 |
| `tail.` | `→` | none | prefix: tail call |
| `throw` | `ref    →` | none | throw exception |
| `unaligned.` | `→` | int8 | prefix: unaligned access |
| `unbox` | `ref    → i` | type | unbox to value type address |
| `unbox.any` | `ref    → 1` | type | unbox, or castclass for reference types |
| `volatile.` | `→` | none | prefix: volatile access |
| `xor` | `1 1    → 1` | none | bitwise xor |

218 opcodes. `calli` takes a signature, `switch` takes a label list, and the prefixes
`constrained.`, `unaligned.`, `volatile.`, `tail.`, and `readonly.` apply to the next instruction.
