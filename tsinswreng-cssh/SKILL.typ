#import "@preview/tsinswreng-auto-heading:0.1.0": auto-heading
#let H = auto-heading
\-\-\-

name: tsinswreng-cssh

description: cssh庫使用說明。用于編寫dotnet-script/csx腳本

\-\-\-

#H[CsSh是甚麼][
	CsSh是類Shell API的封裝庫,
	能增強C\#語言在腳本方面的便利性。
]

#H[在csx腳本使用CsSh時 開頭寫法示例][
	````cs
	#r "nuget: Tsinswreng.CsSh, 0.3.0-alpha"
	#nullable enable
	using Tsinswreng.CsSh;
	using static Tsinswreng.CsSh.ShGlobal;
	using CT = System.Threading.CancellationToken;
	CT Ct = default;

	//腳本正文...
	````
]

#H[多腳本場景下注意避免重複代碼][
	用`#load`抽取復用。
	
	設想這樣一個場景,
	你需要寫很多份csx腳本,
	每個腳本都要操作瀏覽器,
	但各自做不同的瀏覽器操作。
	
	則你可以這樣抽取:
	
	`_Common.csx`:
	```cs
	#r "nuget: Tsinswreng.CsSh, 0.3.0-alpha"
	#r "nuget: PuppeteerSharp, 25.17.1"
	#nullable enable
	using Tsinswreng.CsSh;
	using static Tsinswreng.CsSh.ShGlobal;
	using CT = System.Threading.CancellationToken;
	CT Ct = default;
	
var EdgePath = @"C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
var GlobalBrowser = MkBrowser(Ct);
static async Task<Browser> MkBrowser(CT Ct){
	var browser = await Puppeteer.LaunchAsync(new LaunchOptions {
		Headless = false, ExecutablePath = EdgePath,
		DefaultViewport = new ViewPortOptions { Width = 1600, Height = 900 },
		Args = ["--no-sandbox", "--disable-gpu"],
	});
	return browser;
}
//...
	```
	
	這樣之後你就可以:
	
	`Login.csx`:
	````cs
	#load "_Common.csx"
	//注意 using 是不會隨#load傳播的
	using Tsinswreng.CsSh;
	using static Tsinswreng.CsSh.ShGlobal;
	using CT = System.Threading.CancellationToken;
	
	var browser = await MkBrowser(Ct);//Ct會隨#load傳播, 不需重複定義
	//...
	````
	
	從而可以減少重複代碼。
	
	把以下內容放到全局`_Common.csx`中:
	- 所有導入的包 如CsSh
	- 要復用的變量/函數等
]

#H[編譯運行][
	#H[常規運行][
		`dotnet-script xxx.csx`
		或
		`dotnet script xxx.csx`
	]
	
	#H[無文件 臨時運行][
	```sh
dotnet-script eval <<'CSX'
#r "nuget: Tsinswreng.CsSh, 0.3.0-alpha"
#nullable enable
using Tsinswreng.CsSh;
using static Tsinswreng.CsSh.ShGlobal;
using CT = System.Threading.CancellationToken;
CT Ct = default;

//腳本正文...
Echo($"Hello from CsSh! Current Working Directory is {Pwd()}");
CSX
	```
	]
	
	#H[只編譯檢查不運行][
		`dotnet publish xxx.csx`
		
		因爲你只是編譯檢查,
		所以執行後要刪除編譯產物 防止浪費空間
	]
]

#H[注意事項][
	- #[文件IO異步優先。立即可出結果的(如`IsDir` `IsFile` )不用異步]
	- #[避免自造輪子。優先用現成封裝好的]
	- #[腳本不應依賴pwd,
		- 優先用`CsxDir()`(僅在csx中有效)或`[CallerPath]`(在普通.cs中使用)來定位
		- #[確保不論pwd在何處,
			執行腳本都不影響結果]
	]
	- #[能並行的操作儘量并行, 善用 `Task.WhenAll`等。]
	- #[語法要求
		- #[拼接路徑用重載的 / 如 `Pwd()/"a/b"`,
			禁止用`System.IO.Path.Combine`
		]
		- #[能寫`new()`的就不要寫`new 類型名()`, 尤其是傳那幾個`XxxOptions`之時
			正確示例:
			````cs
			await Cp(src, dst, new(Overwrite: true), Ct);
			````
		]
		- #[儘量用更簡單的列表字面量語法。
			錯誤示例:
			```cs
			await Exe("git", new[]{"commit", "-m", "init"}, Ct);
			```
			`new List<>{}`同樣不要用
			
			正確示例:
			````cs
			await Exe("git", ["commit", "-m", "init"], Ct);
			````
			]
		- #[多用C\#文本塊語法,
		不允許手動轉義。
		錯誤示例:
		```cs
		var LoginJson = $"{{\"username\":\"admin\",\"password\":\"{Password}\"}}"; 
		```
		
		正確示例
		````cs
		var LoginJson = $$"""{"username":"admin","password":"{{Password}}"}""";
		````
		
		]
	]
	
]

#H[CsSh常用API][
````cs
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

````
]

#H[Tsinswreng.CsSh庫的更多API][
	自己看同目錄下的`Api.cs`文件。
	註: 爲省字數, 很多`public`修飾符被省略,
	但實際上這些API仍是public的。
	
	// #raw(
	// 	read("Api.cs"),
	// 	lang: "cs", block: true
	// )
]
