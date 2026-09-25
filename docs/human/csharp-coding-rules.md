1. 禁止修改代码格式（除非影响代码逻辑，例如不当的缩进导致编译错误或者 if 分支执行不完整），这会造成无谓的时间和 token 消耗；格式规约由 dotnet format/csharpier 钩子自动进行。

2. 使用 getter/setter 操作属性时，应使用 C# 14 引入的 `field` 语法，示例：

```csharp
// With C# 14: use the field keyword
public int MaxLength 
{
	get;
	set 
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
		field = value;
	}
}
```

3. 一个方法最好不要超过 40 条语句。
4. 对于大于 3 个字符串拼接，使用字符串插值代替。示例：

```csharp
// GOOD
string result = "Hello " + name;
  

// GOOD
string result = $"Welcome, {firstName} {lastName}!";

// BAD，4 个字符串拼接，大于 3 个
string result = "Welcome, " + firstName + " " + lastName;
  

// BAD，是字符串拼接
string result = string.Format("Welcome, {0} {1}!", firstName, lastName);
  

// BAD，是字符串拼接
string result = string.Concat("Welcome, ", firstName, " ", lastName, "!");  
```

5. 在日志优先使用结构化日志模板，不使用字符串插值和字符串拼接。

```csharp
// GOOD，没有动态参数时直接常量
logger.LogInformation("User logged in")

// GOOD，结构化、可查询、避免无意义字符串构造
logger.LogInformation("User {UserId} logged in", userId)


// BAD，无需插值
logger.LogInformation($"User logged in")

// BAD，丢失结构化字段，日志关闭时仍可能构造字符串
logger.LogInformation($"User {userId} logged in")

// BAD，同上，且可产生中间字符串
logger.LogInformation("User " + userId + " logged in")

// 视框架而定。@ 是 Serilog 风格，不是 MEL 通用语义
logger.LogInformation("User {@User} logged in", user)
```

6. 日志中 Placeholder 使用业务语义名称，而不是位置名称。

```csharp
// GOOD
logger.LogInformation(
    "Order {OrderId} created for user {UserId}",
    order.Id,
    user.Id);

// BAD
logger.LogInformation(
    "Order {Id} created for user {Id2}",
    order.Id,
    user.Id);

// BAD
logger.LogInformation(
    "Order {0} created for user {1}",
    order.Id,
    user.Id);
```

7. 异常使用 `Exception` 参数，不要插值 `exception.Message`

```csharp
// GOOD
logger.LogError(
    exception,
    "Failed to process order {OrderId}",
    orderId);
    
// BAD
logger.LogError($"Failed to process order {orderId}: exception.Message}");
    
```

8. 昂贵参数需要显式判断日志级别

```csharp
// GOOD
if (logger.IsEnabled(LogLevel.Debug))
{
    logger.LogDebug(
        "Request payload: {Payload}",
        Serialize(request));
}

// BAD    
logger.LogDebug("Request payload: {Payload}", Serialize(request));

```

9. 对于使用 Serilog，还需要遵循下面的规则：
- 标量使用 `{Property}`，复杂对象使用 `{@Property}`，`{$Property}` 一般不要用。

```csharp

// 标量
logger.LogInformation(
    "User {UserId} logged in",
    userId);
 
// 复杂对象
logger.LogInformation(
    "Received request {@Request}",
    request);
     
```

- Scope 用于上下文，不要每条日志重复参数

```csharp
// GOOD
using var scope = logger.BeginScope(new Dictionary<string, object?>
{
    ["TenantId"] = tenantId,
    ["DeviceId"] = deviceId
});

logger.LogInformation("Connection established");
logger.LogInformation("Sending configuration");
logger.LogInformation("Connection closed");


// BAD
logger.LogInformation(
    "Connection established {TenantId} {DeviceId}",
    tenantId,
    deviceId);

logger.LogInformation(
    "Sending configuration {TenantId} {DeviceId}",
    tenantId,
    deviceId);
```

注意，Serilog 侧要确保启用了 scope：

```csharp
.Enrich.FromLogContext()
```

10. 对于小型、频繁传递的不可变数据，例如坐标、颜色或日期范围，使用 struct/record struct 代替 class。
11. 若初始化逻辑平凡（或者说仅起到字段赋值作用而无其他逻辑），优先使用主构造函数（primary constructor）代替一般构造函数：

```csharp
// Before primary constructors
public class OrderService 
{
  private readonly IOrderRepository repository;

  public OrderService(IOrderRepository repository)
  {
      this.repository = repository;
  }
}

// With primary constructors
public class OrderService(IOrderRepository repository)
{
	public async Task<Order> GetOrderAsync(Guid id) => await repository.GetByIdAsync(id);
}
```

12. 超过 3 参数的个的 record 或 class，禁止使用主构造函数。

```csharp
// GOOD
public record Point(int X, int Y);

// GOOD
public class User(Guid Id, string Name, string Email);

// BAD
public record User(Guid Id, string Name, string Email, string Phone);

// BAD
public class User(Guid Id, string Name, string Email, string Phone);

// GOOD
public class User
{
	public Guid Id { get; init; }
	public string Name { get; init; }
	public string Email { get; init; }
	public string Phone { get; init; }
}
```

13. 对于无法直接编辑的第三方库，可以考虑使用扩展块（C# 14 引入）和更早之前的扩展方法：

```csharp
// C# 14 extension block
extension(Order order) 
{
	public bool IsOverdue => order.DueDate < DateTimeOffset.UtcNow && !order.IsCompleted; 
	public void MarkAsShipped(DateTimeOffset shippedAt) 
    {
        order.Status = OrderStatus.Shipped;
        order.ShippedAt = shippedAt;
    }
}
```

但不要给你能直接修改的类添加扩展，这属于逻辑浪费。

14. 在泛型类或方法中，不要与 object 类型进行来回转换，而应使用 where 约束或 as 运算符来指定泛型参数的确切特性。例如：

```csharp
class SomeClass {}

// Don't
class MyClass
{
  void SomeMethod(T t)
  {
    object temp = t;
    SomeClass obj = (SomeClass) temp;
  }
}

// Do
class MyClass where T : SomeClass 
{
  void SomeMethod(T t) 
  {
  	SomeClass obj = t;
  }
}
```

15. LINQ 表达式的返回结果应进行物化，例如：

```csharp
var query =
    from customer in db.Customers
    where customer.Balance > GoldMemberThresholdInEuro
    select new GoldMember(customer.Name, customer.Balance);

return query; // LINQ是延迟执行的，所以query实质上是表达式树，而非最终结果
return query.ToList();  //所以需要ToList()、ToArray()方法进行物化
```

16. 何时使用 IEnumerable\<T\>/Span\<T\>（栈上）/Memory\<T\>（堆上）？

```
需要处理数据吗？
├── 是连续内存块（数组、字符串、栈内存、非托管内存）？
│   ├── 方法完全是同步的？
│   │   ├── 需要写入？→ Span<T>
│   │   └── 只读？→ ReadOnlySpan<T>
│   └── 需要跨 await、存为字段、或传入异步流？
│       ├── 需要写入？→ Memory<T>
│       └── 只读？→ ReadOnlyMemory<T>
└── 是离散/惰性序列（数据库行、文件行、无限流）？
    └── 用 IEnumerable<T> / IAsyncEnumerable<T>
```

使用 Span\<T\>/Memory\<T\>提升性能的典例：

| 对比维度    | 传统方式                            | Span / Memory 方式                | 典型提升               |
| ------- | ------------------------------- | ------------------------------- | ------------------ |
| 字符串切片   | `Substring`                     | `ReadOnlySpan<char>.Slice`      | **4\~7.5x 速度，零分配** |
| 字符串分割   | `string.Split`                  | `ReadOnlySpan<char>.Split`      | **\~38% 速度，零分配**   |
| 数组子集    | `Array.Copy` + `new[]`          | `Span<T>.Slice`                 | **O(1) 切片，零分配**    |
| 数字解析    | `int.Parse(str.Substring(...))` | `int.Parse(str.AsSpan(...))`    | **零子串分配**          |
| 小缓冲区    | `new byte[256]`                 | `stackalloc byte[256]` + `Span` | **13x 速度，零堆分配**    |
| GUID 转换 | `Guid.Parse` / `ToString()`     | `Span` 解析/格式化                   | **40\~50% 速度，减分配** |

一些普通操作的速查表：

| 场景               | 推荐类型                                     | 理由                         |
| ---------------- | ---------------------------------------- | -------------------------- |
| 同步字符串/数组处理       | `ReadOnlySpan<T>` / `Span<T>`            | 零分配切片，无 GC 压力              |
| `stackalloc` 缓冲区 | `Span<T>`                                | 唯一支持栈内存的类型                 |
| P/Invoke 同步调用    | `Span<T>`                                | 可直接映射到指针                   |
| 异步 I/O 缓冲区       | `Memory<T>`                              | 可跨 `await`，再转 `Span` 处理    |
| 类字段保存缓冲区         | `Memory<T>`                              | `Span` 不能作为字段              |
| 数据库/文件行遍历        | `IEnumerable<T>` / `IAsyncEnumerable<T>` | 非连续，惰性求值                   |
| 需要 LINQ 操作       | `IEnumerable<T>`                         | `Span`/`Memory` 不支持原生 LINQ |
| 不确定数据是否连续        | `IEnumerable<T>`                         | 最通用的抽象                     |

17. 代码中的字面量（魔数）应以常量声明封装（若用于日志记录和追踪除外），例如：

```csharp
public class Whatever 
{
    public static readonly Color PapayaWhip = new Color(0xFFEFD5); //字面量0xFFEFD5用PapayaWhip声明
    public const int MaxNumberOfWheels = 18;  //字面量18用MaxNumberOfWheels声明
    public const byte ReadCreateOverwriteMask = 0b0010_1100;  //字面量0b0010_1100用ReadCreateOverwriteMask声明
}
```

语义非常明确且不会发生变化的情况也不必封装，例如：

```csharp
mean = (a + b) / 2; // 平均数自然是/2，声明成常量反倒是画蛇添足
WaitMilliseconds(waitTimeInSeconds * 1000); //秒换算成毫秒必须是*1000，声明成常量也是画蛇添足
```

18. 避免使用嵌套的 try catch 块，这会减弱可读性。
19. 不要使用 ref/out 参数，这会降低可读性，返回复合对象、结构体或元组。
   例外情况：

```csharp
bool success = int.TryParse(text, out int number); //使用了TryParse或者依赖库里本身就有ref/out参数，需要保证逻辑正确
```

20. 若字符串含有大量转义字符，需要用原始字符串字面量代替：

```csharp
string pattern = "^(https?:\\/\\/)(www\\.)?[a-zA-Z0-9]+\\.[a-z]+$"; //存在大量转义字符！需要放弃
string pattern = """^(https?:\/\/)(www\.)?[a-zA-Z0-9]+\.[a-z]+$"""; //原始字符串字面量用"""开始和结束，转义字符减少很多
```

21. async/await 用于 I/O 密集型任务，Task.Run()/Task.StartNew() 则用于 CPU（计算）密集型任务。
22. 建议直接 await ValueTask 或者 ValueTask\<T\>，且只等待一次：

```csharp
// OK / GOOD
int bytesRead = await stream.ReadAsync(buffer, cancellationToken);

// OK / GOOD
int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

// OK / GOOD - Get task if you want to overcome the limitations exposed by ValueTask / ValueTask<T>
Task<int> task = stream.ReadAsync(buffer, cancellationToken).AsTask();
```

23. 应尽量使用 C#新语法或者更规范的语法，包括且不限以下的内容：

```csharp
ValueTuple<string, int> tuple = new ValueTuple<string, int>("", 1); //旧语法，不再使用
(string, int) tuple = ("", 1); //推荐的新语法
```

```csharp
Nullable<DateTime> startDate; //旧语法，不再使用
DateTime? startDate; //推荐的新语法
```

```csharp
if (startDate == null) ... //不推荐使用
if (startDate is null) ... //推荐使用，语义上更好理解
```

```csharp
if (startDate.HasValue) ... //不推荐使用
if (startDate is not null) ... //推荐使用，语义上更好理解
```

```csharp
if (startDate.HasValue && startDate.Value > DateTime.Now) ... // startDate.Value > DateTime.Now说明startData有值，没必要再判断一遍
if (startDate > DateTime.Now) ...  // 正确的做法：只做必要的判断
```

```csharp
List<string> items = new List<string>();// 不推荐
List<string> items = [];  // 推荐1: []比new List……更加简单直观
var items = new List<string>(); // 推荐2: 使用 var 声明变量
```

```csharp
if (list == null) list = []; // 旧语法，且易读性低，不再使用
list ??= []; //新语法，推荐使用
```

24. C#支持解构元组，错误和正确示例如下：

```csharp
// 错误示例，写法繁冗
public record Point(int X, int Y);

Point point = GetOrigin();
int x = point.X;
int y = point.Y;
```

```csharp
// 正确示例，写法简单
(int x, int y) = GetOrigin();

// 也可以使用模式匹配解构数组
if (items is [int first, int second, ..])  
{
// use first and second directly
}

//对foreach也适用
foreach ((int key, int value) in dictionary)
{
Console.WriteLine($"{key}: {value}");
}
```

25. 不要使用#region 标记。
26. 写注释时禁止写入被否定的决策，除非它对应的代码曾在真实的运行中出现过 bug。例如：

```
// 注释：白米粥里需要加入大便……
用户驳回：白米粥里为什么要加入大便？
// 注释：白米粥里不要加入大便，理由是大便污染了米粥……   ------> 错！白米粥一开始就不应该有大便！
// 注释：白米粥的米和水体积比约为1：1.2……            --------> 对！说白米粥怎么做就可以了！
// 注释：白米粥里之所以出现大便，是因为用户的狗在碗里拉屎，解决方法：把狗杀了。 ----------> 对！出现过真实的问题（狗在碗里拉屎），并给出解决方式（杀狗）！
```

27. 在写 C#时，除非确实需要控制反转，否则不要总是试图用依赖注入去写逻辑。
28. 除非明确要吞并异常，禁止编写无行为的 try catch 块：

```csharp
try
{
	doSomething();
}
catch 
{
	//do Nothing! 不应该！
}
```

29. 不要直接实例化 HttpClient

所有 HttpClient 的使用，都应该通过 `IHttpClientFactory`，禁止直接使用 `new HttpClient()` 创建 `HttpClient` 实例。

30. 禁止使用 `DateTime `

- 优先使用 `DateTimeOffset`；只要适用，就使用 `TimeProvider`
- API 输出的日期和时间值，需要序列化为具有时区指示符或偏移量（`Z` or `+/-HH:mm`）的 RFC 3339字符串。禁止返回无时区或时间偏移量的本地日期时间字符串。
- 禁止在服务器上进行日期和时间的本地化处理，这些应该在客户端处理。
