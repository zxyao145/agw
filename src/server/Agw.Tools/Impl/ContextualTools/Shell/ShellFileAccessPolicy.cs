using System.Text;
using Microsoft.Agents.AI.Tools.Shell;

namespace Agw.Tools.Impl.ContextualTools.Shell;

/// <summary>
/// 拒绝与 file_access_* 工具能力重复的 shell 命令：读取、列出、搜索、创建、写入、编辑、删除文件的程序，以及对文件的重定向、
/// here-document 和交给解释器的内联代码。命令行按引号、管道、逻辑运算符、子 shell 与命令替换切分成简单命令逐个判断。
/// Rejects shell commands that duplicate the file_access_* tools: programs that read, list, search, create, write,
/// edit or delete files, plus redirections to or from files, here-documents and inline code handed to interpreters.
/// The command line is split into simple commands by quotes, pipes, logical operators, subshells and command
/// substitutions, and each one is checked.
/// </summary>
internal static class ShellFileAccessPolicy
{
    private const string UseFileAccessTools =
        "Use file_access_read, file_access_read_lines, file_access_ls, file_access_grep, file_access_write, "
        + "file_access_replace, file_access_replace_lines or file_access_delete instead.";

    /// <summary>
    /// 用途就是访问文件的程序，包括 POSIX 工具与 PowerShell、cmd 的对应命令。
    /// Programs whose purpose is file access, including POSIX utilities and their PowerShell and cmd counterparts.
    /// </summary>
    private static readonly HashSet<string> FileAccessPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        // 读取 / read
        "cat",
        "tac",
        "head",
        "tail",
        "less",
        "more",
        "nl",
        "od",
        "xxd",
        "hexdump",
        "strings",
        "bat",
        "view",
        "vi",
        "vim",
        "nvim",
        "nano",
        "emacs",
        "ed",
        "ex",
        "type",
        "gc",
        "get-content",
        "gi",
        "get-item",
        // 列出 / list
        "ls",
        "dir",
        "vdir",
        "tree",
        "find",
        "fd",
        "fdfind",
        "locate",
        "gci",
        "get-childitem",
        // 搜索 / search
        "grep",
        "egrep",
        "fgrep",
        "rg",
        "ag",
        "ack",
        "sls",
        "select-string",
        "findstr",
        // 创建、写入、复制、移动 / create, write, copy, move
        "touch",
        "tee",
        "cp",
        "mv",
        "install",
        "dd",
        "truncate",
        "ln",
        "mkdir",
        "mkfifo",
        "mknod",
        "rename",
        "split",
        "csplit",
        "md",
        "ni",
        "new-item",
        "sc",
        "set-content",
        "ac",
        "add-content",
        "out-file",
        "clc",
        "clear-content",
        "cpi",
        "copy-item",
        "copy",
        "mi",
        "move-item",
        "move",
        "ren",
        "rni",
        "rename-item",
        "xcopy",
        "robocopy",
        // 删除 / delete
        "rm",
        "rmdir",
        "unlink",
        "shred",
        "ri",
        "remove-item",
        "del",
        "erase",
        "rd",
    };

    /// <summary>
    /// 只在带文件操作数时才等同于文件访问的过滤程序；通过管道处理其他命令输出时允许。
    /// Filter programs that only amount to file access when given file operands; allowed when processing piped output.
    /// </summary>
    private static readonly HashSet<string> FilterPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "sort",
        "uniq",
        "cut",
        "paste",
        "diff",
        "cmp",
        "comm",
        "join",
        "fold",
        "rev",
        "column",
        "base64",
        "expand",
        "unexpand",
    };

    /// <summary>
    /// 第一个操作数是脚本的过滤程序：第二个操作数起视为文件；带原地编辑或脚本文件选项时拒绝。
    /// Filter programs whose first operand is a script: further operands are files; in-place or script-file options
    /// are rejected.
    /// </summary>
    private static readonly HashSet<string> ScriptFilterPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "sed",
        "awk",
        "gawk",
        "mawk",
        "nawk",
    };

    /// <summary>
    /// 解释器及其内联代码选项；内联代码可以读写文件，因此被拒绝，运行脚本文件则允许。
    /// Interpreters and their inline-code options; inline code can read or write files and is rejected, while running
    /// a script file is allowed.
    /// </summary>
    private static readonly Dictionary<string, string[]> InterpreterInlineOptions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["sh"] = ["-c"],
        ["bash"] = ["-c"],
        ["zsh"] = ["-c"],
        ["dash"] = ["-c"],
        ["ksh"] = ["-c"],
        ["fish"] = ["-c", "--command"],
        ["pwsh"] = ["-c", "-command", "-e", "-ec", "-encodedcommand"],
        ["powershell"] = ["-c", "-command", "-e", "-ec", "-encodedcommand"],
        ["cmd"] = ["/c", "/k"],
        ["python"] = ["-c"],
        ["python3"] = ["-c"],
        ["perl"] = ["-e", "-E", "-i", "-p", "-n"],
        ["ruby"] = ["-e"],
        ["node"] = ["-e", "--eval", "-p", "--print"],
        ["php"] = ["-r"],
        ["lua"] = ["-e"],
    };

    /// <summary>
    /// 只是转发给另一个程序的包装命令；真正被判断的是它们后面的程序。
    /// Wrapper commands that merely forward to another program; the program after them is what gets judged.
    /// </summary>
    private static readonly HashSet<string> WrapperPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo",
        "doas",
        "env",
        "nice",
        "nohup",
        "time",
        "timeout",
        "command",
        "exec",
        "builtin",
        "xargs",
        "stdbuf",
        "ionice",
    };

    private static readonly HashSet<string> AllowedRedirectionTargets = new(StringComparer.Ordinal)
    {
        "/dev/null",
        "/dev/stdout",
        "/dev/stderr",
        "nul",
    };

    /// <summary>
    /// 创建交给 shell 执行器的策略；执行器在运行前对每条命令求值。
    /// Creates the policy handed to the shell executor, which evaluates every command before running it.
    /// </summary>
    public static ShellPolicy CreateShellPolicy() =>
        new(
            denyList: null,
            allowList: null,
            custom: request => FindViolation(request.Command) is { } reason ? ShellPolicyOutcome.Deny(reason) : null
        );

    /// <summary>
    /// 返回命令违反约束的原因；允许时返回 null。
    /// Returns why the command violates the restriction, or null when it is allowed.
    /// </summary>
    public static string? FindViolation(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new Analyzer().Analyze(command);
    }

    private static string ProgramViolation(string program) =>
        $"'{program}' reads, lists, searches, writes, edits or deletes files, which is reserved for the file access tools. {UseFileAccessTools}";

    private static string RedirectionViolation() =>
        $"Redirecting shell input or output to a file duplicates file_access_read and file_access_write; only /dev/null and file descriptor duplication such as 2>&1 are allowed. {UseFileAccessTools}";

    private static string HereDocumentViolation() =>
        $"Here-documents, here-strings and process substitution feed file content through the shell. {UseFileAccessTools}";

    private static string InterpreterViolation(string program) =>
        $"Inline code passed to '{program}' can read or write files; run a script file or use the file access tools. {UseFileAccessTools}";

    /// <summary>
    /// 一次命令行分析：按 shell 的引号与运算符规则切分，命令替换递归分析。
    /// One command line analysis: splits by shell quoting and operator rules, analyzing command substitutions
    /// recursively.
    /// </summary>
    private sealed class Analyzer
    {
        private readonly List<string> _words = [];
        private readonly StringBuilder _word = new();
        private bool _hasWord;
        private string? _violation;

        public string? Analyze(string text)
        {
            var index = 0;
            while (index < text.Length && _violation == null)
            {
                var current = text[index];
                switch (current)
                {
                    case ' ' or '\t' or '\r':
                        EndWord();
                        index++;
                        continue;
                    case '\n' or ';' or '(' or ')':
                        EndWord();
                        EndCommand();
                        index++;
                        continue;
                    case '|':
                        EndWord();
                        EndCommand();
                        index += index + 1 < text.Length && text[index + 1] == '|' ? 2 : 1;
                        continue;
                    case '&':
                        if (index + 1 < text.Length && text[index + 1] == '>')
                        {
                            EndWord();
                            index = ParseRedirection(text, index + 1);
                            continue;
                        }

                        EndWord();
                        EndCommand();
                        index += index + 1 < text.Length && text[index + 1] == '&' ? 2 : 1;
                        continue;
                    case '<' or '>':
                        index = ParseRedirection(text, index);
                        continue;
                    case '#' when !_hasWord:
                        while (index < text.Length && text[index] != '\n')
                        {
                            index++;
                        }

                        continue;
                    case '\'':
                        index = ParseSingleQuoted(text, index + 1);
                        continue;
                    case '"':
                        index = ParseDoubleQuoted(text, index + 1);
                        continue;
                    case '`':
                        index = ParseBacktickSubstitution(text, index + 1);
                        continue;
                    case '$' when index + 1 < text.Length && text[index + 1] == '(':
                        index = ParseDollarSubstitution(text, index + 2);
                        continue;
                    case '\\':
                        if (index + 1 < text.Length)
                        {
                            if (text[index + 1] != '\n')
                            {
                                AppendToWord(text[index + 1]);
                            }

                            index += 2;
                        }
                        else
                        {
                            index++;
                        }

                        continue;
                    default:
                        AppendToWord(current);
                        index++;
                        continue;
                }
            }

            EndWord();
            EndCommand();
            return _violation;
        }

        private void AppendToWord(char value)
        {
            _word.Append(value);
            _hasWord = true;
        }

        private void EndWord()
        {
            if (_hasWord)
            {
                _words.Add(_word.ToString());
                _word.Clear();
                _hasWord = false;
            }
        }

        private void EndCommand()
        {
            if (_words.Count > 0)
            {
                _violation ??= CheckSimpleCommand(_words);
                _words.Clear();
            }
        }

        private int ParseSingleQuoted(string text, int index)
        {
            _hasWord = true;
            while (index < text.Length && text[index] != '\'')
            {
                _word.Append(text[index]);
                index++;
            }

            return index + 1;
        }

        private int ParseDoubleQuoted(string text, int index)
        {
            _hasWord = true;
            while (index < text.Length && text[index] != '"')
            {
                var current = text[index];
                if (current == '\\' && index + 1 < text.Length)
                {
                    _word.Append(text[index + 1]);
                    index += 2;
                    continue;
                }

                if (current == '`')
                {
                    index = ParseBacktickSubstitution(text, index + 1);
                    continue;
                }

                if (current == '$' && index + 1 < text.Length && text[index + 1] == '(')
                {
                    index = ParseDollarSubstitution(text, index + 2);
                    continue;
                }

                _word.Append(current);
                index++;
            }

            return index + 1;
        }

        private int ParseBacktickSubstitution(string text, int index)
        {
            var end = text.IndexOf('`', index);
            if (end < 0)
            {
                end = text.Length;
            }

            AnalyzeNested(text[index..end]);
            return end + 1;
        }

        /// <summary>
        /// 解析 "$(" 之后的内容：算术展开 "$((...))" 跳过，命令替换递归分析。
        /// Parses what follows "$(": arithmetic expansion "$((...))" is skipped and command substitution is analyzed
        /// recursively.
        /// </summary>
        private int ParseDollarSubstitution(string text, int index)
        {
            var arithmetic = index < text.Length && text[index] == '(';
            var depth = 1;
            var end = index;
            while (end < text.Length && depth > 0)
            {
                switch (text[end])
                {
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                    case '\'':
                        end = text.IndexOf('\'', end + 1);
                        if (end < 0)
                        {
                            end = text.Length;
                        }

                        break;
                }

                end++;
            }

            if (!arithmetic)
            {
                AnalyzeNested(text[index..Math.Max(index, end - 1)]);
            }

            // 替换结果作为当前词的一部分，保证 "$(...)" 本身不会被当作程序名。
            // The substitution result becomes part of the current word so "$(...)" itself is never a program name.
            AppendToWord('$');
            return end;
        }

        private void AnalyzeNested(string inner)
        {
            _violation ??= new Analyzer().Analyze(inner);
        }

        /// <summary>
        /// 解析从 <c>&lt;</c> 或 <c>&gt;</c> 开始的重定向；只允许 /dev/null 类目标与文件描述符复制。
        /// Parses a redirection starting at <c>&lt;</c> or <c>&gt;</c>; only /dev/null-like targets and file descriptor
        /// duplication are allowed.
        /// </summary>
        private int ParseRedirection(string text, int index)
        {
            // 紧贴在运算符前面的纯数字词是文件描述符编号，例如 "2>&1"。
            // A purely numeric word right before the operator is a file descriptor number, as in "2>&1".
            if (_hasWord && _word.ToString().All(char.IsAsciiDigit))
            {
                _word.Clear();
                _hasWord = false;
            }
            else
            {
                EndWord();
            }

            var op = text[index];
            index++;
            var doubled = index < text.Length && text[index] == op;
            if (doubled)
            {
                index++;
                if (op == '<')
                {
                    _violation ??= HereDocumentViolation();
                    return SkipToLineEnd(text, index);
                }
            }

            if (index < text.Length && (text[index] == '|' || text[index] == '&' && op == '>'))
            {
                if (
                    text[index] == '&'
                    && index + 1 < text.Length
                    && (char.IsAsciiDigit(text[index + 1]) || text[index + 1] == '-')
                )
                {
                    return SkipDescriptor(text, index + 1);
                }

                index++;
            }
            else if (index < text.Length && text[index] == '&' && op == '<')
            {
                return SkipDescriptor(text, index + 1);
            }

            if (index < text.Length && text[index] == '(')
            {
                _violation ??= HereDocumentViolation();
                return index;
            }

            while (index < text.Length && (text[index] == ' ' || text[index] == '\t'))
            {
                index++;
            }

            var target = new StringBuilder();
            while (index < text.Length && !" \t\r\n;|&<>()".Contains(text[index]))
            {
                if (text[index] == '\'' || text[index] == '"')
                {
                    var quote = text[index];
                    var end = text.IndexOf(quote, index + 1);
                    if (end < 0)
                    {
                        end = text.Length;
                    }

                    target.Append(text, index + 1, end - index - 1);
                    index = end + 1;
                    continue;
                }

                target.Append(text[index]);
                index++;
            }

            if (!AllowedRedirectionTargets.Contains(target.ToString()))
            {
                _violation ??= RedirectionViolation();
            }

            return index;
        }

        private static int SkipDescriptor(string text, int index)
        {
            while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] == '-'))
            {
                index++;
            }

            return index;
        }

        private static int SkipToLineEnd(string text, int index)
        {
            while (index < text.Length && text[index] != '\n')
            {
                index++;
            }

            return index;
        }

        private static string? CheckSimpleCommand(IReadOnlyList<string> words)
        {
            var index = 0;
            while (index < words.Count && IsAssignment(words[index]))
            {
                index++;
            }

            while (index < words.Count)
            {
                var program = ProgramName(words[index]);
                if (WrapperPrograms.Contains(program))
                {
                    index = SkipWrapper(words, index + 1, program);
                    continue;
                }

                if (FileAccessPrograms.Contains(program))
                {
                    return ProgramViolation(program);
                }

                var operands = words.Skip(index + 1).ToArray();
                if (ScriptFilterPrograms.Contains(program))
                {
                    var hasEditingOption = operands.Any(static operand =>
                        operand is "-i" or "--in-place" or "-f" or "--file"
                        || operand.StartsWith("-i", StringComparison.Ordinal)
                        || operand.StartsWith("--in-place=", StringComparison.Ordinal)
                    );
                    return hasEditingOption || CountFileOperands(operands) >= 2 ? ProgramViolation(program) : null;
                }

                if (FilterPrograms.Contains(program))
                {
                    return CountFileOperands(operands) >= 1 ? ProgramViolation(program) : null;
                }

                if (InterpreterInlineOptions.TryGetValue(program, out var inlineOptions))
                {
                    var usesInlineCode = operands.Any(operand =>
                        operand == "-" || inlineOptions.Contains(operand, StringComparer.OrdinalIgnoreCase)
                    );
                    return usesInlineCode ? InterpreterViolation(program) : null;
                }

                return null;
            }

            return null;
        }

        /// <summary>
        /// 跳过包装命令自身的选项，返回被包装程序的位置；xargs 没有后续程序时默认调用 echo，允许。
        /// Skips the wrapper's own options and returns the position of the wrapped program; xargs without a program
        /// defaults to echo, which is allowed.
        /// </summary>
        private static int SkipWrapper(IReadOnlyList<string> words, int index, string wrapper)
        {
            var skipNext = false;
            while (index < words.Count)
            {
                var word = words[index];
                if (skipNext)
                {
                    skipNext = false;
                    index++;
                    continue;
                }

                if (word == "--")
                {
                    return index + 1;
                }

                if (word.StartsWith('-'))
                {
                    skipNext = WrapperOptionsWithValues.Contains(word);
                    index++;
                    continue;
                }

                if (IsAssignment(word) && wrapper is "env" or "sudo" or "doas")
                {
                    index++;
                    continue;
                }

                if (wrapper == "timeout")
                {
                    // timeout 的第一个操作数是时长。The first operand of timeout is the duration.
                    return index + 1;
                }

                return index;
            }

            return index;
        }

        private static readonly HashSet<string> WrapperOptionsWithValues = new(StringComparer.Ordinal)
        {
            "-u",
            "-g",
            "-C",
            "-n",
            "-I",
            "-L",
            "-P",
            "-s",
            "-d",
            "-a",
            "-E",
            "-S",
            "--user",
            "--group",
            "--chdir",
            "--max-args",
            "--max-lines",
            "--max-procs",
            "--replace",
            "--delimiter",
            "--arg-file",
            "--signal",
            "--kill-after",
            "-k",
            "-i",
            "-o",
            "-e",
        };

        private static bool IsAssignment(string word)
        {
            var equals = word.IndexOf('=');
            return equals > 0
                && word.AsSpan(0, equals).ToArray().All(static c => char.IsAsciiLetterOrDigit(c) || c == '_')
                && !char.IsAsciiDigit(word[0]);
        }

        /// <summary>
        /// 统计看起来是文件的操作数：跳过选项、纯数字（如 "-k 2" 的 2）和单个字符（如 "-d ','" 的分隔符）。
        /// Counts operands that look like files: options, pure numbers (the 2 in "-k 2") and single characters (the
        /// delimiter in "-d ','") are skipped.
        /// </summary>
        private static int CountFileOperands(IReadOnlyList<string> operands) =>
            operands.Count(static operand =>
                !operand.StartsWith('-') && operand.Length > 1 && !operand.All(char.IsAsciiDigit)
            );

        /// <summary>
        /// 取程序名：去掉目录、Windows 可执行文件后缀与 bash 的反斜杠前缀。
        /// Extracts the program name: strips directories, Windows executable suffixes and bash's backslash prefix.
        /// </summary>
        private static string ProgramName(string word)
        {
            var name = word.TrimStart('\\');
            var separator = name.LastIndexOfAny(['/', '\\']);
            if (separator >= 0)
            {
                name = name[(separator + 1)..];
            }

            foreach (var suffix in (string[])[".exe", ".cmd", ".bat", ".com"])
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^suffix.Length];
                    break;
                }
            }

            return name;
        }
    }
}
