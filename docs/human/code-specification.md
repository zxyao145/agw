你必须牢记：**代码首先是写给人看的，其次才是给机器执行的。**

## 命名

名字应该表达**意图**，而不是表达实现。**如果需要用注释解释变量是什么意思，首先应该考虑是不是名字没取好。**

- 变量、函数、类等的命名需要有意义。

```
// bad
var id = GetSignedInUserId();
var flag = true;

// better
var userId = GetSignedInUserId();
var retryFlag = true;
```

- 禁止自造词，或者使用不常见的缩写。
- 使用可搜索的名称。若静态变量或常量可能在代码中多处使用，则应赋其以便于搜索的名称。
- 不必用前缀来标明成员变量是什么类型。

```
// bad
private string m_desc;

// better
private string desc;
```

- 在作用域较小、也没有名称冲突时，循环计数器可以使用 i, j, k 这类命名。但是如果嵌套循环层数比较多，则禁止使用。
- 对于有行为的类名和对象名应该是名词或名词短语，例如：Producer、Consumer、User；对于无行为或少于 3 个方法的 record、dto 或 data class，可以使用 Item、Info、Data 之类的命名。
- 方法名应当是动词或动词短语，如 postPayment、deletePage 或 save。
- 每个抽象概念、术语，应该从始至终使用一个词，避免前后修改。

```
// bad
var xxxTask = CreateXxxJob();

// better，前后都使用 job
var xxxJob = CreateXxxJob();
```

## 函数与方法

编写规则：让业务流程在较高抽象层次上可读，而把实现细节压下去。

- 函数按照职责区分，可以为两类：
	- 数据和逻辑处理：尽可能的小，而且只做一件事。一般在程序的内层深处。
	- 流程编排（Orchestration）：流程控制，没有 if-else 分支（但是可以有 if 中断条件 的判断，用来中断流程）。一般在程序的入口处。

```
// bad，随着业务增长，这个方法很容易膨胀成几百行。
public async Task CreateUserAsync(CreateUserRequest request)
{
    Validate(request);

    var user = new User(...);

    await db.Users.AddAsync(user);
    await db.SaveChangesAsync();

    await emailService.SendWelcomeEmail(user.Email);

    logger.LogInformation("User created...");
}

// better，通过抽取函数，让顶层代码接近于描述业务流程。
public async Task CreateUserAsync(CreateUserRequest request)
{
    ValidateRequest(request);

    var user = CreateUser(request);

    await SaveUserAsync(user);
    await SendWelcomeEmailAsync(user);
}

// OK，中断流程
boolean withholdSuccess = inventoryService.withhold(cmd.getItemId(), cmd.getQuantity());
if (!withholdSuccess) {
            return Result.fail("Inventory not enough");
        }
```

- 数据和逻辑处理类的函数，如果没有特殊需要，不要使用 try、catch 捕捉错误。
- 数据和逻辑处理类的函数，如果没有特殊需要，使用异常替代错误码的返回。抛出异常之前，使用 warn 或 error 级别的日志，打印异常原因。
- 每行代码的长度，80 个字符以内为佳，禁止超过 120 个字符。
- 方法的行数，抛出 `{`，`}` 等字符外，20 行内为佳，禁止超过 40 行。
- `if`、`while` 等判断语句中，如果判断条件比较复杂或超过 1 行，应该使用单独的变量或函数来处理。
- 避免使用否定性的判断条件。

```
// bad
if(!enable) {}

// better
if(disable) {}
```

- 向下原则。
	- 如果函数语言没有要求，应按照从上到下的顺序编写。
	- private 函数的声明，应该紧挨着使用它的 public 函数的下方。
	- 一个 public 函数如果使用了多个 private 函数，按照使用顺序从上到下排列。
- 概念相关的代码，应该放在一起。
	- 局部变量声明，应该在距离使用这个变量最近的地方声明；
	- 方法的返回值，可以在方法顶部声明。
	- golang 中，defer 可以紧挨着变量声明的地方。
	- 概念相关的代码，前后各使用一行空行分割。
- 函数的参数最好不要超过 5 个。超过应使用 data class、record 等封装。
- 对于支持多返回值的语言，函数的返回值禁止超过 3 个，超过应使用 data class、record 等封装。
- 如果没有特殊需要，使用异常替代错误码的返回。抛出异常之前，使用 warn 或 error 级别的日志，打印异常原因。
- 禁止重复编写内容相同的代码。
- 如果没有很复杂的逻辑、极其严格的性能要求，或者其他特殊的需要，禁止使用 goto 语句。
- 方法之间，使用 2 个空行分割。

## 类

- 类表示的封装，是一组行为关联性很高的方法载体，内部的成员应该高耦合。
- 类应该遵守单一职责原则（SRP）。
- 非 data class、record 类中，public 的属性少于 10 个为佳，禁止超过 20 个。
- public 函数少于 10 个为佳，禁止超过 20 个。超过后应该进行分组，使用组合的形式替代直接使用。
- 类的继承深度应该比较小，小于等于 3 层为佳，禁止超过 5 层。
- 组合大于继承。超过 3 层的继承深度，考虑使用组合的形式，可以是组合类，也可以是继承接口实现组合。

## 注释

- **注释不是越多越好**。通常为流程和核心逻辑写注释，而不是逐行描述做什么。
- 注释以 3 行以内为佳，禁止超过 5 行
- 禁止翻译专业术语**。注释中，专业术语应该保持原样，禁止翻译。比如禁止将 token 翻译为 词元。
- 注释一般用来说明这里是干什么的、为什么这么做。注释如果用于写警告，需要写明原因。
- **注释内容是给人看的，应该使用“人话”去写**。注释可以使用领域术语，但是禁止使用难以理解的词语，应该让一个普通的程序员都能了解注释内容。
- 不允许使用 " 不是...而是..." 句式
- 如果不需要对比的话，就不要对比；
- 不要再任何话说完之后都提一句 " 不是其他的 xxx"
- 为某个决策写注释的时候，需要注明时间、原因。
- TODO 注释用来描述待定的工作，或者未来的规划设计等，并附加介绍当前为什么这么做。
- 方法、类等注释，应该使用函数语言自己的结构化文档注释格式，比如 XMLdoc（C#）、Javadoc（java）、TSDoc / JSDoc（TypeScript / JavaScript）、KDoc（Kotlin）、GoDoc（Golang）、rustdoc（Rust）。
- 无需自动为每个 方法、类等添加注释。
- 行注释以添加到当前行的上面为佳，禁止添加到行的尾部。
- 写提示词和注释的时候，一句话一行，例如：
```text
你需要做如下工作，做完报告给我。
```

禁止出现明明是一句话却要强行换行的情况：

```text
你需要做如下工作，
做完报告给我。
```


## 异常

 - 优先使用异常而不是错误码。
 - 优先使用自定义类型异常，而不是使用 Message 来区分异常。

```
// bad
throw new Exception("user not signIn");


// better
public class UserNotSignInException : Exception {}
throw new UserNotSignInException("user not signIn");
```

 - 错误处理不应该污染正常业务逻辑。
	- **正常执行路径应该清晰。** 异常情况、错误处理、资源释放等不要把主要业务流程淹没。
- 捕获的异常，必须使用 log 打印出来，而且必须打印出堆栈。对于具体类型的异常，至少使用 warn 级别，对于使用 Exception 捕获的异常，至少使用 Error 级别。

```
// example
try
{
	var userId = GetSignedInUserId();
}
catch (UnAuthenticationException e)
{
	logger.Warn(e, "The user does not have a signin");
}
catch (Exception exp)
{
	logger.Error(exp, "Unknown exception occurred while obtaining login user ID");
}
```

- 禁止 catch 异常后直接抛出；在非最外层的 catch，禁止只打印一行日志后，继续抛出。

```
try
{
	var userId = GetSignedInUserId();
}
catch (UnAuthenticationException e)
{
    // bad
	throw e;
}
```

## 日志

- 使用结构化日志，禁止字符串拼接/插值。

```
// bad
_logger.LogInformation($"User {userId} logged in from {ip}");
_logger.LogInformation("User " + userId + " logged in");

// better
_logger.LogInformation("User {UserId} logged in from ClientIp}", userId, ip);
```

- Exception 不要只打印 Message，需要同时打印堆栈。

```
// bad
catch (Exception ex)
{
    _logger.LogError(ex.Message);
}

// better
catch (Exception ex)
{
    _logger.LogError(
        ex,
        "Failed to process message {MessageId}",
        messageId);
}

```

- 不要重复记录同一个异常。在同一个请求链路中，禁止每一层都重复 catch 和 打印日志。异常通常只在“被处理”或者“不会再继续向上传播”的边界记录一次。
- 给日志建立统一字段命名。

```
// nad
userId
UserID
uid
user_id
userid
```

- 高频路径不要随便打 Information。
- 循环里面警惕日志的量。
- 禁止记录敏感数据，例如 Password、AccessToken、PrivateKey 等。
- Payload 默认不打印。

```
// bad
_logger.LogInformation("Calling API: {@Request}", request);

// better
_logger.LogInformation(
    "Calling {RemoteService} operation {Operation} for {DeviceId}",
    "Sonos",
    "GetPlayer",
    deviceId);
```

- 对于构造成本较高的日志参数，必须在构造前使用 `IsEnabled` 判断日志级别，避免在日志未启用时执行序列化、集合展开、字符串拼接等昂贵操作。

```
// bad
_logger.LogDebug("Response: {Response}", JsonSerializer.Serialize(response));
    
// better
if (_logger.IsEnabled(LogLevel.Debug))
{
    _logger.LogDebug("Response: {Response}", JsonSerializer.Serialize(response));
}
```

## 其他

- 使用空格替换 `tab` 缩紧，不同语言使用的空格数量不同，依照项目规范来定。如果没有声明，默认使用 4 个空格。
- 不要新增只被调用一次的小 helper；只有当它能命名清楚概念、隔离复杂逻辑、复用已有边界或改善测试时才抽函数。
- 作为对上一条的补充，如果同样的逻辑出现大于 3 次，就应考虑作为共享函数，并写入项目记忆以供继续复用（如果有项目记忆的话）。
- 新模块应围绕职责和变化原因命名，避免按“工具集合”“杂项”“common”堆放无关能力。
- **测试代码不得复制/重写业务逻辑**。测试只负责组装输入（数据、文件路径、配置）和断言输出，**完全复用**业务逻辑，不重新实现业务逻辑，也不可自己绕过业务逻辑。
- 只为可独立重用或复杂到需要隔离的逻辑编写单元测试。对于其余逻辑，对整个功能进行集成测试或组件测试可以提供更好的覆盖率，且维护成本更低。
- **确认废弃的代码一律删除**，不要想着"保留工作"或者"兜底"。重构时先查调用方，确认零调用即删；测试、参数、兼容层随机制一并删除。
- 作为对上一条的补充，如果是用户注释的代码，禁止主动移除。
- 除非特别说明，否则禁止在 API路由中使用 Path Parameter。应通过 Query Parameter 或 Body 传递数据。