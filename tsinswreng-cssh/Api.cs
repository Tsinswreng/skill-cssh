using System.Diagnostics;
using System.IO.Pipelines;

namespace Tsinswreng.CsSh;

/// 一条尚未启动的外部命令。
/// Cmd 创建该对象不会执行进程；首次异步读取 Result.Stdout、Result.Stderr，或等待 Done 时才启动一次。
/// 命令实现会同时排空 stdout 与 stderr，防止调用方暂未消费其中一条流时造成子进程 pipe 阻塞。
sealed class Command:IDisposable,IAsyncDisposable{
	/// 未指定 CommandOptions.PipeBufferSizeBytes 時，每一條 stdout 或 stderr Pipe 使用的預設容量。
	/// 此值限制每條輸出在消費者尚未讀取時可暫存的資料量；兩條 Pipe 各自使用此容量。
	const long DefaultPipeBufferSizeBytes = 64 * 1024;

	/// 建立命令時凍結的可執行檔、參數、工作目錄、環境、標準流與錯誤策略。
	readonly CommandRunOptions Options;
	/// 接收子程序標準輸出的暫存管線；讀取 Result.Stdout 時由此取得資料。
	readonly Pipe StdoutPipe;
	/// 接收子程序標準錯誤的暫存管線；讀取 Result.Stderr 時由此取得資料。
	readonly Pipe StderrPipe;
	/// 儲存唯一的命令退出結果或執行例外，避免多個觀察者重複完成任務。
	readonly TaskCompletionSource<CommandExit> ExitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
	/// 保護啟動、釋放與 Process 欄位的同步鎖，避免命令被並行啟動或釋放。
	readonly object Gate = new();
	/// 唯一的背景啟動工作；null 代表命令尚未被觀察，因而尚未啟動。
	Task? StartTask;
	/// 已啟動的 .NET Process；完成或釋放後設為 null，避免持有原生控制代碼。
	Process? Process;
	/// 標示 Command 已釋放；釋放後不得再啟動新的子程序。
	bool IsDisposed;
	/// 僅供 Sh 以完整執行配置建立命令。
	Command(CommandRunOptions Options);

	/// 命令产生的两条标准输出流。
	CommandResult Result{get;}

	/// 等待子进程退出并取得退出结果。
	/// Cmd 创建的命令以非零退出码结束时，此任务抛出 CommandFailedException；TryCmd 创建的命令始终返回结果。
	Task<CommandExit> Done{
		get{
			EnsureStarted();
			return ExitTask;
		}
	}

	/// 尚未觀察命令時也可被釋放；實作以此欄位保存唯一的退出工作。
	readonly Task<CommandExit> ExitTask;

	/// 让 await Command 等价于 await Command.Done。
	/// 等待该对象会启动命令，但不会自动转送 Result 中的输出；要转送时使用 Out。
	System.Runtime.CompilerServices.TaskAwaiter<CommandExit> GetAwaiter();

	/// 非同步消費命令結果：stdout 寫入 Sh.Stdout，stderr 寫入 Sh.Stderr，並等待命令退出。
	Task<CommandExit> Out(CT Ct);

	/// 非同步消費命令結果：stdout 與 stderr 都寫入 Target，並等待命令退出。
	/// 兩條流會並行讀取、分段序列化寫入，因此有限 Pipe 不會因其中一條暫未被讀取而卡住。
	Task<CommandExit> Out(Content Target, CT Ct);

	/// 非同步消費命令結果：stdout 與 stderr 都覆寫寫入 TargetPath，並等待命令退出。
	/// 這是腳本將整條命令輸出寫入檔案的簡寫；TargetPath 的父目錄會自動建立。
	Task<CommandExit> Out(Pth TargetPath, CT Ct);

	/// 非同步消費命令結果：分別指定 stdout 與 stderr 的輸出目標，並等待命令退出。
	Task<CommandExit> Out(Content Stdout, Content Stderr, CT Ct);

	/// 完整讀取 stdout 與 stderr 文字，並等待命令退出。
	/// 這是終端消費操作，適用於 JSON、版本資訊等有限輸出；大量輸出應直接消費 Result 中的 Content 流。
	Task<CommandTextResult> Text(CT Ct);

	/// 释放结果流的临时资源；进程尚未结束时同时终止该进程。
	void Dispose();

	/// 异步释放结果流的临时资源；进程尚未结束时同时终止该进程。
	ValueTask DisposeAsync();

}

/// 建立 Command 時的可選配置。
/// 每個設定只影響本次命令；不會改變 Sh 實例或目前程序的全域狀態。
sealed record CommandOptions(
	/// 子程序的標準輸入來源；null 代表不重定向 stdin，子程序沿用宿主程序的標準輸入。
	Content? Stdin = null,
	/// 子程序工作目錄；null 時使用建立此 Command 的 Sh 目前工作目錄。
	Pth? Cwd = null,
	/// 僅套用於子程序的環境變數覆寫；鍵的值為 null 時從子程序環境移除該鍵。
	IReadOnlyDictionary<string, string?>? Env = null,
	/// 每一條 stdout 或 stderr Pipe 的最大暫存位元組數；null 時使用 Command.DefaultPipeBufferSizeBytes。
	/// 此值必須大於 0；Pipe 滿時會以背壓暫停上游輸出，而非無限制佔用記憶體。
	long? PipeBufferSizeBytes = null);

/// Command 的內部執行配置。
/// 此 record 在建立 Command 時完成快照，避免呼叫端在命令啟動前修改參數或環境集合而改變行為。
sealed record CommandRunOptions(
	/// 要啟動的可執行檔名稱或路徑；不經由 Shell 字串解析。
	string Exe,
	/// 子程序參數；每個元素是一個完整參數，會直接加入 ProcessStartInfo.ArgumentList。
	IReadOnlyList<string> Args,
	/// 呼叫端提供的選用設定；保留 stdin 來源與原始選項供命令生命週期使用。
	CommandOptions Options,
	/// 已展開為絕對路徑的子程序工作目錄。
	Pth Cwd,
	/// 建立命令當下取得的子程序環境快照；不會受後續宿主環境變更影響。
	IReadOnlyDictionary<string, string> Environment,
	/// Exe 或 Command.Out() 未指定目標時接收子程序 stdout 的預設 Content。
	Content Stdout,
	/// Exe 或 Command.Out() 未指定目標時接收子程序 stderr 的預設 Content。
	Content Stderr,
	/// 取消命令啟動、資料轉送與等待的取消令牌。
	CT Ct,
	/// true 時非零退出碼會讓 Done 以 CommandFailedException 失敗；false 時回傳失敗的 CommandExit。
	bool ThrowOnError);

/// Command 产生的标准输出。
/// 两条 Stream 均为只读、惰性流：首次 ReadAsync 会启动所属命令；数据边产生边可读，不预先全部载入内存。
sealed record CommandResult(
	/// 子程序標準輸出；只能按 Stream 的一次性消費規則讀取。
	Content Stdout,
	/// 子程序標準錯誤；只能按 Stream 的一次性消費規則讀取。
	Content Stderr);

/// 完整消費命令文字輸出後取得的結構化結果。
/// Stdout 與 Stderr 各自保留，Exit 保留退出碼、耗時與成功狀態。
sealed record CommandTextResult(
	/// 完整消費 stdout 後以文字解碼得到的內容；只適合有限大小輸出。
	string Stdout,
	/// 完整消費 stderr 後以文字解碼得到的內容；只適合有限大小輸出。
	string Stderr,
	/// 命令退出碼、耗時及成功狀態。
	CommandExit Exit);

/// 子进程退出后的结构化结果。
sealed record CommandExit(
	/// 子程序回報的原始退出碼；0 代表成功。
	int ExitCode,
	/// 從 Process.Start 成功到子程序退出的耗時。
	TimeSpan Duration,
	/// ExitCode 是否為 0 的便利判斷值。
	bool IsSuccess);

/// Sh.Cmd 遇到非零退出码时抛出的异常。
sealed class CommandFailedException:Exception{
	/// 失败命令退出时的结果。
	CommandExit Exit{get;}

	/// 由失败命令及执行结果创建异常。
	CommandFailedException(CommandExit Exit);
}
namespace Tsinswreng.CsSh;

/// 讀取前啟動所屬 Command 的只讀 Stream 包裝。
/// 包裝不緩衝資料，讀取會直接進入 PipeReader 對應的非同步 Stream。

using System.Text;

namespace Tsinswreng.CsSh;

/// CsSh 的統一資料載體。
/// 一個 Content 包裝一條可讀或可寫的資料流；它可由文字或普通 Stream 隱式建立，
/// 因此檔案、命令與標準輸入輸出 API 都只需要處理 Content。
/// Content 是一次性資料：轉成 string、讀取其 Stream，或作為 Write 的來源都會推進底層位置。
sealed class Content:IDisposable,IAsyncDisposable{
	/// 此 Content 所包裝的普通 .NET Stream。
	/// 取得它不複製資料，也不重設目前讀寫位置。
	Stream Stream{get;}

	/// 建立時的文字編碼與底層 Stream 所有權設定。
	ContentOptions Options{get;}


	/// 從普通 .NET Stream 建立 Content。
	Content(Stream Stream, ContentOptions? Options = null);

	/// 將文字編碼為 Content；預設 UTF-8。
	static implicit operator Content(string Text) {
		ArgumentNullException.ThrowIfNull(Text);
		var Bytes = Encoding.UTF8.GetBytes(Text);
		return new(new MemoryStream(Bytes, writable: false), new(LeaveOpen: false));
	}

	/// 將普通 .NET Stream 包裝為 Content，不預先讀取或複製資料。
	static implicit operator Content(Stream Stream) {
		return new(Stream);
	}

	/// 同步讀完 Content 並按設定編碼解碼為文字。
	/// 這會消費 Content；需要非同步讀取時使用 Text(Ct)。
	static implicit operator string(Content Value) {
		ArgumentNullException.ThrowIfNull(Value);
		return Value.Text();
	}

	/// 取出 Content 的底層普通 Stream，不複製資料。
	static implicit operator Stream(Content Value) {
		ArgumentNullException.ThrowIfNull(Value);
		return Value.Stream;
	}

	/// 同步讀完並解碼為文字。
	string Text();

	/// 非同步讀完並解碼為文字；Ct 必須作為最後一個位置參數傳入。
	Task<string> Text(CT Ct);

	/// 釋放 Content 擁有的底層資源；不擁有的外部 Stream 不會被關閉。
	void Dispose();

	/// 非同步釋放 Content 擁有的底層資源；不擁有的外部 Stream 不會被關閉。
	ValueTask DisposeAsync();



}

/// Content 的建立選項。
/// Encoding 控制 string 與 Content 的互轉；LeaveOpen 為 true 時 Content 釋放後保留呼叫方提供的 Stream。
sealed record ContentOptions(
	Encoding? Encoding = null,
	bool LeaveOpen = true);
namespace Tsinswreng.CsSh;

class Sh{
	/// 取得当前进程的环境变量；不存在时返回 null。
	string? GetEnv(string Name);

	/// 设置当前进程的环境变量。
	/// 若只需影响一次外部调用，应在创建 X 命令时传入专用环境变量设置，避免污染脚本后续步骤。
	void SetEnv(string Name, string Value);

	/// 从当前进程的环境变量中移除指定名称。
	void UnsetEnv(string Name);
}
namespace Tsinswreng.CsSh;

/// Adds the path-join operator to ordinary strings for Cssh scripts.
/// The result always uses forward slashes, so it can be supplied unchanged to every Cssh path API.
static class ExtnString{
	/// Extends string without introducing a wrapper type at script call sites.
	extension(string z){
		/// Joins two path segments without duplicating the separator at their boundary.
		/// An empty segment is ignored; a leading slash in B is treated as a separator rather than a new rooted path.
		static Pth operator /(string A, string B){
			return Pth.Join(A, B);
		}
	}
}
namespace Tsinswreng.CsSh;

/// Cp 的可选行为。
sealed record CpOptions(
	bool Overwrite = false);

/// Mv 的可选行为。
sealed record MvOptions(
	bool Overwrite = false);

/// Ls、LsDir 與 LsFile 的可选行为。
/// Recursive 為 false 時只列出直接子項；為 true 時遞迴列出所有後代項目。
sealed record LsOptions(
	bool Recursive = false);

/// 供 csx 构建脚本直接静态导入的文件系统动词。
/// 每个路径均由 .NET 负责平台适配，不依赖 Bash、PowerShell 或外部 coreutils。
/// 调用方可统一使用正斜杠，例如 Rm("artifacts/publish")；无需调用 Path.Combine。
class Sh{
	/// 判断路径是否存在，可以是文件或目录。
	bool Exists(Pth Path);

	/// 异步判断路径是否存在；Ct 必须作为最后一个位置参数传入。
	Task<bool> Exists(Pth Path, CT Ct);

	/// 取得文件或目录的 .NET 文件系统信息；路径不存在时返回 null。
	/// 文件返回 FileInfo，目录返回 DirectoryInfo；其余属性直接使用 BCL 成员读取。
	FileSystemInfo? FsInfo(Pth Path);

	/// 异步取得文件或目录的 .NET 文件系统信息；路径不存在时返回 null。
	Task<FileSystemInfo?> FsInfo(Pth Path, CT Ct);

	/// 判断路径是否为普通文件；不存在时返回 false。
	/// 与 FsInfo 一样，遇到真实的文件系统访问错误会抛出异常。
	bool IsFile(Pth Path);

	/// 异步判断路径是否为普通文件；不存在时返回 false。
	Task<bool> IsFile(Pth Path, CT Ct);

	/// 判断路径是否为目录；不存在时返回 false。
	/// 与 FsInfo 一样，遇到真实的文件系统访问错误会抛出异常。
	bool IsDir(Pth Path);

	/// 异步判断路径是否为目录；不存在时返回 false。
	Task<bool> IsDir(Pth Path, CT Ct);

	/// 建立目录及所有缺失的父目录；已存在时不报错。
	void Mkdir(Pth Path);

	/// 异步建立目录；Ct 必须作为最后一个位置参数传入。
	Task<nil> Mkdir(Pth Path, CT Ct);

	/// 删除文件，或递归删除目录。
	/// 固定等价于 Bash 的 rm -rf：目标不存在时忽略，绝不交互确认；权限不足等真实文件系统错误仍会抛出。
	void Rm(Pth Path);

	/// 异步删除文件或目录；语义与同步 Rm 相同，Ct 必须作为最后一个位置参数传入。
	Task<nil> Rm(Pth Path, CT Ct);

	/// 复制文件，或递归复制整个目录树。
	/// Source 也可为 glob；例如 Cp("ExternalRsrc/*", "output") 等价于 Bash 的 cp -r ExternalRsrc/* output。
	void Cp(Pth Source, Pth Destination, CpOptions? Options = null);

	/// 异步复制文件、目录或 glob 匹配项；Ct 必须作为最后一个位置参数传入。
	Task<nil> Cp(Pth Source, Pth Destination, CT Ct);

	/// 异步复制文件或目录；需要覆写既有目标时传入 Options；Ct 必须作为最后一个位置参数传入。
	Task<nil> Cp(Pth Source, Pth Destination, CpOptions? Options, CT Ct);

	/// 移动文件或目录；Overwrite 控制目标已存在时是否替换。
	void Mv(Pth Source, Pth Destination, MvOptions? Options = null);

	/// 异步移动文件或目录；Ct 必须作为最后一个位置参数传入。
	Task<nil> Mv(Pth Source, Pth Destination, CT Ct);

	/// 异步移动文件或目录；需要覆写既有目标时传入 Options；Ct 必须作为最后一个位置参数传入。
	Task<nil> Mv(Pth Source, Pth Destination, MvOptions? Options, CT Ct);
	
	/// 取得路徑的最後一段檔案或目錄名稱。
	/// 採用 System.IO.Path 的元件擷取語義；根路徑會回傳空字串。
	Pth BaseName(Pth Path);

	/// 取得路徑的父目錄部分。
	/// 採用 System.IO.Path 的元件擷取語義；沒有父目錄或僅為根路徑時回傳空字串。
	Pth DirName(Pth Path);

	/// 依此 Sh 的目前工作目錄展開為絕對、規範化路徑。
	/// 不要求目標存在，且不解析符號連結；等價於 System.IO.Path.GetFullPath。
	Pth FullPath(Pth Path);

	/// 惰性查找匹配的文件系统路径；相对 Pattern 按当前 Sh 的工作目录解析。
	/// 语法与匹配语义由 Meziantou.Framework.Globbing 的 Standard dialect 定义，支持 *、?、[]、{} 与 **。
	/// 不以 / 结尾的模式只匹配文件；以 / 结尾的模式只匹配目录，且字面量目录如 Glob("src/assets/") 会返回该目录本身。
	/// 返回完整 Pth，不建立 FileSystemInfo；需要类型或 metadata 时，显式调用 IsFile、IsDir 或 FsInfo。
	/// 列举在 foreach 时才访问文件系统，所以目录不存在、权限不足等错误也会在 foreach 时抛出。
	/// <example>
	/// foreach (var FilePath in Glob("src/**/*.cs")) {
	/// 	Console.WriteLine(FilePath);
	/// }
	/// foreach (var DirectoryPath in Glob("src/*/")) {
	/// 	Console.WriteLine(DirectoryPath);
	/// }
	/// </example>
	IEnumerable<Pth> Glob(Pth Pattern);

	/// 惰性列出 Path 的文件和子目录路径，等价于 Bash 的 ls；Path 为 null 时列出当前 Sh 的工作目录。
	/// 直接转发 Directory.EnumerateFileSystemEntries，因此只产生路径，不读取大小、时间等 metadata。
	/// <example>
	/// foreach (var EntryPath in Ls("artifacts")) {
	/// 	Console.WriteLine(EntryPath);
	/// }
	/// </example>
	IEnumerable<Pth> Ls(Pth? Path = null, LsOptions? Options = null);

	/// 惰性列出 Path 的子目录路径；Path 为 null 时列出当前 Sh 的工作目录。
	/// 直接转发 Directory.EnumerateDirectories；使用 new LsOptions(Recursive: true) 可递迴列出所有子目录。
	/// <example>
	/// foreach (var DirectoryPath in LsDir("src", new LsOptions(Recursive: true))) {
	/// 	Console.WriteLine(DirectoryPath);
	/// }
	/// </example>
	IEnumerable<Pth> LsDir(Pth? Path = null, LsOptions? Options = null);

	/// 惰性列出 Path 的文件路径；Path 为 null 时列出当前 Sh 的工作目录。
	/// 直接转发 Directory.EnumerateFiles；使用 new LsOptions(Recursive: true) 可遞迴列出所有檔案。
	/// <example>
	/// foreach (var FilePath in LsFile("src")) {
	/// 	Console.WriteLine(FilePath);
	/// }
	/// </example>
	IEnumerable<Pth> LsFile(Pth? Path = null, LsOptions? Options = null);

	/// 讀取檔案為 Content；呼叫方可將結果隱式視為 string 或普通 Stream。
	Content Read(Pth Path);

	/// 非同步讀取檔案為 Content；Ct 必須作為最後一個位置參數傳入。
	Task<Content> Read(Pth Path, CT Ct);

	/// 覆寫檔案內容，等價於 > Path。
	/// Source 接受 Content；string 與普通 Stream 均可隱式轉入 Content。
	void Write(Pth Path, Content Source);

	/// 以字串路徑覆寫檔案內容。
	/// 此重載優先保留 Write("file", "text") 的腳本寫法，避免與 Content 目標寫入重載二義。
	void Write(string Path, Content Source);

	/// 非同步覆寫檔案內容；Ct 必須作為最後一個位置參數傳入。
	Task<nil> Write(Pth Path, Content Source, CT Ct);

	/// 以字串路徑非同步覆寫檔案內容；Ct 必須作為最後一個位置參數傳入。
	Task<nil> Write(string Path, Content Source, CT Ct);

	/// 追加檔案內容，等價於 >> Path。
	/// Source 接受 Content；string 與普通 Stream 均可隱式轉入 Content。
	void Append(Pth Path, Content Source);

	/// 以字串路徑追加檔案內容。
	void Append(string Path, Content Source);

	/// 非同步追加檔案內容；Ct 必須作為最後一個位置參數傳入。
	Task<nil> Append(Pth Path, Content Source, CT Ct);

	/// 以字串路徑非同步追加檔案內容；Ct 必須作為最後一個位置參數傳入。
	Task<nil> Append(string Path, Content Source, CT Ct);
}
namespace Tsinswreng.CsSh;

/// 表示供 Cssh API 使用的檔案系統路徑。
/// 此類型保留與 string 的隱式互轉，以維持腳本中既有的字面量呼叫方式。
readonly struct Pth{
	/// 路徑的原始字串值。
	string Value{get;}

	/// 以原始字串建立路徑。
	Pth(string Value);

	/// 取得供 BCL 與一般文字 API 使用的原始路徑值。
	static implicit operator string(Pth z) {
		return z.Value;
	}

	/// 讓既有字串字面量可直接傳入 Cssh 的路徑 API。
	static implicit operator Pth(string z) {
		return new Pth(z);
	}

	/// 將右側路徑片段附加到目前路徑，並統一使用正斜線。
	static Pth operator /(Pth A, string B) {
		return Join(A.Value, B);
	}

	/// 將兩個路徑附加為一條 Cssh 路徑。
	static Pth operator /(Pth A, Pth B) {
		return Join(A.Value, B.Value);
	}

	/// 對兩段路徑做不改變根語意的字串層級連接。
	static Pth Join(string A, string B);

	/// 以原始路徑值表示此實例，供記錄與插值字串使用。
	override string ToString();
}
namespace Tsinswreng.CsSh;

/// Cssh 的一个脚本上下文实例。
/// 每个实例拥有自己的当前目录与标准流；脚本通常通过 ShGlobal 的 using static facade 使用默认实例。
class Sh{
	/// 建立一个以当前进程目录为初始目录的 Shell 上下文。
	Sh();

	/// 当前脚本进程的标准输入内容。
	Content Stdin{get;}

	/// 当前脚本进程的标准输出内容。
	Content Stdout{get;}

	/// 当前脚本进程的标准错误内容。
	Content Stderr{get;}

	/// 跨平台的空内容目标，等价于 Bash 的 /dev/null 或 Windows 的 NUL。
	Content Null{get;}

	/// 取得当前进程的工作目录。
	/// 返回路径统一使用正斜杠，因而可直接与 Cssh 的所有路径 API 拼接。
	Pth Pwd();

	/// 取得当前 csx 脚本所在的目录。
	/// 与 Pwd 不同：无论调用者从哪个工作目录启动 dotnet script，此值均指向脚本文件的父目录。
	/// 返回路径统一使用正斜杠。
	Pth CsxDir();

	/// 同步输出一行文本到标准输出，等价于 Bash 的 echo。
	void Echo(string Text);

	/// 异步输出一行文本到标准输出。
	Task<nil> Echo(string Text, CT Ct);

	/// 切换当前进程的工作目录，等价于 Bash 的 cd。
	/// Cssh 的所有路径参数均接受正斜杠；例如 Cd("src/app") 在 Windows、Linux 与 macOS 上含义相同。
	/// 此变更影响后续相对路径和未显式传入 Cwd 的 X 命令。
	void Cd(Pth Path);

	/// 建立尚未啟動的命令；Args 的每一項是一個完整參數，不需呼叫方處理引號或跳脫。
	Command Cmd(string Exe, IList<string> Args);

	/// 建立尚未啟動的命令；Args 的每一項是一個完整參數。
	Command Cmd(string Exe, IList<string> Args, CommandOptions Options);

	/// 非同步建立尚未啟動的命令；Ct 必須作為最後一個位置參數。
	Command Cmd(string Exe, IList<string> Args, CT Ct);

	/// 非同步建立尚未啟動的命令；Args 的每一項是一個完整參數，Ct 必須作為最後一個位置參數。
	Command Cmd(string Exe, IList<string> Args, CommandOptions Options, CT Ct);

	/// 建立不因非零退出碼丟例外的命令；Args 的每一項是一個完整參數。
	Command TryCmd(string Exe, IList<string> Args);

	/// 建立帶可選設定且不因非零退出碼丟例外的命令。
	Command TryCmd(string Exe, IList<string> Args, CommandOptions Options);

	/// 非同步建立不因非零退出碼丟例外的命令；Ct 必須作為最後一個位置參數。
	Command TryCmd(string Exe, IList<string> Args, CT Ct);

	/// 非同步建立帶可選設定且不因非零退出碼丟例外的命令；Ct 必須作為最後一個位置參數。
	Command TryCmd(string Exe, IList<string> Args, CommandOptions Options, CT Ct);

	/// 執行以參數列表描述的命令；Args 不需呼叫方處理引號或跳脫。
	CommandExit Exe(string FileName, IList<string> Args);

	/// 以可選設定執行參數列表命令。
	CommandExit Exe(string FileName, IList<string> Args, CommandOptions Options);

	/// 非同步執行參數列表命令；Ct 必須作為最後一個位置參數。
	Task<CommandExit> Exe(string FileName, IList<string> Args, CT Ct);

	/// 非同步以可選設定執行參數列表命令；Ct 必須作為最後一個位置參數。
	Task<CommandExit> Exe(string FileName, IList<string> Args, CommandOptions Options, CT Ct);

	/// 執行參數列表命令；非零退出碼回傳 CommandExit，不丟例外。
	CommandExit TryExe(string FileName, IList<string> Args);

	/// 以可選設定執行參數列表命令；非零退出碼不丟例外。
	CommandExit TryExe(string FileName, IList<string> Args, CommandOptions Options);

	/// 非同步執行參數列表命令；非零退出碼不丟例外。
	Task<CommandExit> TryExe(string FileName, IList<string> Args, CT Ct);

	/// 非同步以可選設定執行參數列表命令；非零退出碼不丟例外。
	Task<CommandExit> TryExe(string FileName, IList<string> Args, CommandOptions Options, CT Ct);

	/// 將一個值轉成可嵌入字串命令的單一安全參數。
	/// 新程式優先使用 Args 列表重載；Q 適用於保留 Bash 風格命令字串的腳本。
	string Q(string Value);


	/// 非同步地将 Source 复制到 Target。
	/// 读取命令的 Result.Stdout 或 Result.Stderr 会触发命令执行；Source 结束时不会关闭调用方提供的 Target。
	void Write(Content Target, Content Source);

	/// 非同步地将 Source 复制到 Target；Ct 必须作为最后一个位置参数传入。
	Task<nil> Write(Content Target, Content Source, CT Ct);

	/// 非同步合并多条 Source 流并写入同一 Target。
	/// Cssh 串行化对 Target 的写入，因此可安全实现 stdout 与 stderr 合并，而不并发写同一 Stream。
	void Write(Content Target, IReadOnlyList<Content> Sources);

	/// 非同步合并多条 Source 流并写入同一 Target；Ct 必须作为最后一个位置参数传入。
	Task<nil> Write(Content Target, IReadOnlyList<Content> Sources, CT Ct);

	/// 將 CsSh 路徑表示統一為正斜線，不進行絕對化或檔案系統存取。
	static string NormalizePath(string Path);
}
namespace Tsinswreng.CsSh;

/// 全局默认 Shell 实例的静态 facade，供 csx 使用 using static 简化调用。
static class ShGlobal{
	static Sh Sh{get;set;} = new();
	static Pth Pwd();
	static Pth CsxDir();
	static void Echo(string Text);
	static Task<nil> Echo(string Text, CT Ct);
	static void Cd(Pth Path);
	static Command Cmd(string Exe, IList<string> Args);
	static Command Cmd(string Exe, IList<string> Args, CommandOptions Options);
	static Command Cmd(string Exe, IList<string> Args, CT Ct);
	static Command Cmd(string Exe, IList<string> Args, CommandOptions Options, CT Ct);
	static Command TryCmd(string Exe, IList<string> Args);
	static Command TryCmd(string Exe, IList<string> Args, CommandOptions Options);
	static Command TryCmd(string Exe, IList<string> Args, CT Ct);
	static Command TryCmd(string Exe, IList<string> Args, CommandOptions Options, CT Ct);
	static CommandExit Exe(string Exe, IList<string> Args);
	static CommandExit Exe(string Exe, IList<string> Args, CommandOptions Options);
	static Task<CommandExit> Exe(string Exe, IList<string> Args, CT Ct);
	static Task<CommandExit> Exe(string Exe, IList<string> Args, CommandOptions Options, CT Ct);
	static CommandExit TryExe(string Exe, IList<string> Args);
	static CommandExit TryExe(string Exe, IList<string> Args, CommandOptions Options);
	static Task<CommandExit> TryExe(string Exe, IList<string> Args, CT Ct);
	static Task<CommandExit> TryExe(string Exe, IList<string> Args, CommandOptions Options, CT Ct);
	static string Q(string Value);
	static void Write(Content Target, Content Source);
	static Task<nil> Write(Content Target, Content Source, CT Ct);
	static void Write(Content Target, IReadOnlyList<Content> Sources);
	static Task<nil> Write(Content Target, IReadOnlyList<Content> Sources, CT Ct);
	static string? GetEnv(string Name);
	static void SetEnv(string Name, string Value);
	static void UnsetEnv(string Name);
	static bool Exists(Pth Path);
	static Task<bool> Exists(Pth Path, CT Ct);
	static FileSystemInfo? FsInfo(Pth Path);
	static Task<FileSystemInfo?> FsInfo(Pth Path, CT Ct);
	static bool IsFile(Pth Path);
	static Task<bool> IsFile(Pth Path, CT Ct);
	static bool IsDir(Pth Path);
	static Task<bool> IsDir(Pth Path, CT Ct);
	static void Mkdir(Pth Path);
	static Task<nil> Mkdir(Pth Path, CT Ct);
	static void Rm(Pth Path);
	static Task<nil> Rm(Pth Path, CT Ct);
	static void Cp(Pth Source, Pth Destination, CpOptions? Options = null);
	static Task<nil> Cp(Pth Source, Pth Destination, CT Ct);
	static Task<nil> Cp(Pth Source, Pth Destination, CpOptions? Options, CT Ct);
	static void Mv(Pth Source, Pth Destination, MvOptions? Options = null);
	static Task<nil> Mv(Pth Source, Pth Destination, CT Ct);
	static Task<nil> Mv(Pth Source, Pth Destination, MvOptions? Options, CT Ct);
	static Pth BaseName(Pth Path);
	static Pth DirName(Pth Path);
	static Pth FullPath(Pth Path);
	/// 在預設 Sh 的目前工作目錄下惰性列舉 Glob 模式；不以 / 結尾匹配檔案，以 / 結尾匹配目錄。
	/// 例如 foreach (var FilePath in Glob("src/**/*.cs")) { Console.WriteLine(FilePath); }。
	static IEnumerable<Pth> Glob(Pth Pattern);
	/// 在預設 Sh 的目前工作目錄下惰性列舉檔案與目錄路徑；Path 為 null 時列舉目前工作目錄。
	/// 例如 foreach (var EntryPath in Ls("artifacts")) { Console.WriteLine(EntryPath); }。
	static IEnumerable<Pth> Ls(Pth? Path = null, LsOptions? Options = null);
	/// 在預設 Sh 的目前工作目錄下惰性列舉目錄路徑；傳入 new LsOptions(Recursive: true) 可遞迴。
	/// 例如 foreach (var DirectoryPath in LsDir("src")) { Console.WriteLine(DirectoryPath); }。
	static IEnumerable<Pth> LsDir(Pth? Path = null, LsOptions? Options = null);
	/// 在預設 Sh 的目前工作目錄下惰性列舉檔案路徑；傳入 new LsOptions(Recursive: true) 可遞迴。
	/// 例如 foreach (var FilePath in LsFile("src")) { Console.WriteLine(FilePath); }。
	static IEnumerable<Pth> LsFile(Pth? Path = null, LsOptions? Options = null);
	static Content Read(Pth Path);
	static Task<Content> Read(Pth Path, CT Ct);
	static void Write(Pth Path, Content Source);
	static void Write(string Path, Content Source);
	static Task<nil> Write(Pth Path, Content Source, CT Ct);
	static Task<nil> Write(string Path, Content Source, CT Ct);
	static void Append(Pth Path, Content Source);
	static void Append(string Path, Content Source);
	static Task<nil> Append(Pth Path, Content Source, CT Ct);
	static Task<nil> Append(string Path, Content Source, CT Ct);
}
