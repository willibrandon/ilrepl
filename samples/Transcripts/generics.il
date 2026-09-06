// A generic cell, generic methods, and !!0 in member references.
.typeparams (T)
.typeargs (string)
.locals init (!!T value)
ldstr "bound"
unbox.any !!T
stloc value
ldloc value
box !!T
ret

.reset
ldc.i4 3
newarr string
dup
ldc.i4 0
ldstr "x"
stelem.ref
call !!0 [System.Linq]System.Linq.Enumerable::First<string>(class [System.Runtime]System.Collections.Generic.IEnumerable`1<!!0>)
ret
