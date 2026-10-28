#import "@preview/tsinswreng-auto-heading:0.1.0": auto-heading
#let H = auto-heading
\-\-\-

name: tsinswreng-cssh

description: cssh庫使用說明。用于編寫dotnet-script/csx腳本

\-\-\-


#H[Tsinswreng.CsSh庫的API][

	註: 爲省字數, 下文中很多`public`修飾符被省略,
	但實際上這些API仍是public的。

	#raw(
		read(
			"Api.cs",
		),
		lang: "cs",
		block: true,
	)
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

#H[注意事項][
	- #[文件IO異步優先。立即可出結果的(如`IsDir` `IsFile` )不用異步]
	- #[避免自造輪子。優先用現成封裝好的]
	- #[腳本不應依賴pwd,
		- 優先用`CsxDir()`(僅在csx中有效)或`[CallerPath]`(在普通.cs中使用)來定位
		- #[確保不論pwd在何處,
			執行腳本都不影響結果]
	]
	- #[語法要求
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
