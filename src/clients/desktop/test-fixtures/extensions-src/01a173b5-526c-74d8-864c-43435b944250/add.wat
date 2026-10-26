;; 构建时用 wabt 编译为 app.wasm。Compiled to app.wasm with wabt at build time.
(module
  (func (export "add") (param $a i32) (param $b i32) (result i32)
    local.get $a
    local.get $b
    i32.add))
