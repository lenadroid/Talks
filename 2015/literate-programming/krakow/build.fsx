#I @"packages/FsReveal/fsreveal/"
#I @"packages/FAKE/tools/"
#I @"packages/Suave/lib/net40"

#r "FakeLib.dll"
#r "suave.dll"

#load "fsreveal.fsx"

// --------------------------------------------------------------------------------------
// Custom FSI Evaluator fun
// --------------------------------------------------------------------------------------

open Fake
open FSharp.Literate
open FSharp.Markdown

#load @"packages\FsLab\FsLab.fsx"
#load "Formatters.fs"
let fsiEvaluator1 = FsiEvaluator() 
let fsiEvaluator = FsLab.Formatters.wrapFsiEvaluator fsiEvaluator1 "." (System.IO.Path.Combine(__SOURCE_DIRECTORY__,"output")) "G4"

let transformation (value:obj, typ:System.Type) : MarkdownParagraph list option =
  let toHtml = typ.GetMethod("ToHtml") 
  if toHtml <> null then
    let html = toHtml.Invoke(value, [||]) :?> string
    Some [ MarkdownParagraph.InlineBlock html ]
  else None

fsiEvaluator1.RegisterTransformation(transformation)
fsiEvaluator1.EvaluationFailed.Add(fun e ->
  traceImportant <| sprintf "Evaluation failed: %s" e.StdErr
)

// --------------------------------------------------------------------------------------

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted
let gitOwner = "myGitUser"
let gitHome = "https://github.com/" + gitOwner
// The name of the project on GitHub
let gitName = "MyProject"

open FsReveal
open Fake
open Fake.Git
open System.IO
open System.Diagnostics
open Suave
open Suave.Web
open Suave.Http
open Suave.Http.Files

let outDir = __SOURCE_DIRECTORY__ @@ "output"

Target "Clean" (fun _ ->
    CleanDirs [outDir]
)

let copyPics() =
    try
      CopyDir (outDir @@ "images")  (__SOURCE_DIRECTORY__ @@ "slides/images") (fun _ -> true)
    with
    | exn -> traceImportant <| sprintf "Could not copy picture: %s" exn.Message    

let generateFor (file:FileInfo) = 
    try
        copyPics()
        let rec tryGenerate trials =
            try
                let outputFileName = file.Name.Replace(file.Extension,".html")
                match file.Extension with   
                | ".md" ->  FsReveal.GenerateOutputFromMarkdownFile(outDir, outputFileName, file.FullName)
                | ".fsx" -> FsReveal.GenerateOutputFromScriptFile(outDir, outputFileName, file.FullName, fsiEvaluator)
                | _ -> ()
            with 
            | exn when trials > 0 -> tryGenerate (trials - 1)
            | exn -> 
                traceImportant <| sprintf "Could not generate slides for: %s" file.FullName
                traceImportant exn.Message

        tryGenerate 3
    with
    | :? FileNotFoundException as exn ->
        traceImportant <| sprintf "Could not copy file: %s" exn.FileName

let handleWatcherEvents (e:FileSystemEventArgs) =
    let fi = fileInfo e.FullPath 
    traceImportant <| sprintf "%s was changed." fi.Name
    match fi.Attributes.HasFlag FileAttributes.Hidden || fi.Attributes.HasFlag FileAttributes.Directory with
            | true -> ()
            | _ -> generateFor fi

let startWebServer () =
    let serverConfig = 
        { default_config with
           home_folder = Some (System.IO.Path.Combine(__SOURCE_DIRECTORY__, outDir))
        }
    let app =
        Writers.set_header "Cache-Control" "no-cache, no-store, must-revalidate"
        >>= Writers.set_header "Pragma" "no-cache"
        >>= Writers.set_header "Expires" "0"
        >>= browse'
    web_server_async serverConfig app |> snd |> Async.Start
    Process.Start "http://localhost:8083/index.html" |> ignore

Target "GenerateSlides" (fun _ ->
    !! (__SOURCE_DIRECTORY__ @@ "slides/*.md")
      ++ (__SOURCE_DIRECTORY__ @@ "slides/*.fsx")
    |> Seq.map fileInfo
    |> Seq.iter generateFor
)

Target "KeepRunning" (fun _ ->
    use watcher = new FileSystemWatcher(DirectoryInfo(__SOURCE_DIRECTORY__ @@ "slides").FullName,"*.*")
    watcher.EnableRaisingEvents <- true
    watcher.IncludeSubdirectories <- true
    watcher.Changed.Add(handleWatcherEvents)
    watcher.Created.Add(handleWatcherEvents)
    watcher.Renamed.Add(handleWatcherEvents)

    startWebServer ()

    traceImportant "Waiting for slide edits. Press any key to stop."

    System.Console.ReadKey() |> ignore

    watcher.EnableRaisingEvents <- false
    watcher.Dispose()
)

Target "ReleaseSlides" (fun _ ->
    let tempDocsDir = __SOURCE_DIRECTORY__ @@ "temp/gh-pages"
    CleanDir tempDocsDir
    Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") "gh-pages" tempDocsDir

    fullclean tempDocsDir
    CopyRecursive "output" tempDocsDir true |> tracefn "%A"
    StageAll tempDocsDir
    Git.Commit.Commit tempDocsDir "Update generated slides"
    Branches.push tempDocsDir
)

"Clean"
  ==> "GenerateSlides"
  ==> "KeepRunning"

"GenerateSlides"
  ==> "ReleaseSlides"
  
RunTargetOrDefault "KeepRunning"