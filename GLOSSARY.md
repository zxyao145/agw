# Agw

Agw 的领域术语与定义。
Domain terms and definitions for Agw.

## Language

**extension（扩展）**:
为 Agw 提供附加功能的独立扩展单元，可以包含一个或多个运行平台的功能。
An independently packaged unit that adds capabilities to Agw across one or more runtime targets.

**ExtensionTarget（扩展运行平台）**:
extension 声明包含功能的平台，例如 Server、Desktop，以及未来可能支持的 Web、Mobile；一个 extension 可以面向多个运行平台。
A platform for which an extension declares capabilities, such as Server or Desktop; an extension may target multiple platforms.

**Server extension 停机安装（Server extension offline installation）**:
在 Server 停机期间安装、更新和移除 extension，启动后提供启用和停用能力。
Installing, updating, and removing extensions while the Server is stopped, with enable and disable available during operation.
