#r "paket: groupref netcorebuild //"
#load ".fake/build.fsx/intellisense.fsx"

#nowarn "52"

open System
open System.IO
open System.Text.RegularExpressions
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.IO.FileSystemOperators
open Fake.Tools.Git

#if MONO
// prevent incorrect output encoding (e.g. https://github.com/fsharp/FAKE/issues/1196)
System.Console.OutputEncoding <- System.Text.Encoding.UTF8
#endif

let srcFiles =
    !! "./src/Thot.Json/Thot.Json.fsproj"
    ++ "./src/Thot.Json.Net/Thot.Json.Net.fsproj"
    ++ "./src/Thot.Http/Thot.Http.fsproj"

let testsGlob = "tests/**/*.fsproj"
let docFile = "./docs/Docs.fsproj"

module Util =

    let visitFile (visitor: string->string) (fileName : string) =
        File.ReadAllLines(fileName)
        |> Array.map (visitor)
        |> fun lines -> File.WriteAllLines(fileName, lines)

    let replaceLines (replacer: string->Match->string option) (reg: Regex) (fileName: string) =
        fileName |> visitFile (fun line ->
            let m = reg.Match(line)
            if not m.Success
            then line
            else
                match replacer line m with
                | None -> line
                | Some newLine -> newLine)

// Module to print colored message in the console
module Logger =
    let consoleColor (fc : ConsoleColor) =
        let current = Console.ForegroundColor
        Console.ForegroundColor <- fc
        { new IDisposable with
              member x.Dispose() = Console.ForegroundColor <- current }

    let warn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.DarkYellow in printf "%s" s) str
    let warnfn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.DarkYellow in printfn "%s" s) str
    let error str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Red in printf "%s" s) str
    let errorfn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Red in printfn "%s" s) str

let yarn args =
    let code =
        Process.execSimple
            (fun info ->
                { info with
                    FileName = "yarn"
                    Arguments = args
                }
            )
            (TimeSpan.FromMinutes 10.)
    if code <> 0 then
        failwithf "Yarn exited with code: %i" code
    else
        ()

let mono workingDir args =
    let code =
        Process.execSimple
            (fun info ->
                { info with
                    FileName = "mono"
                    WorkingDirectory = workingDir
                    Arguments = args
                }
            )
            (TimeSpan.FromMinutes 10.)
    if code <> 0 then
        failwithf "Mono exited with code: %i" code
    else
        ()

Target.create "Clean" (fun _ ->
    !! "src/**/bin"
    ++ "src/**/obj"
    ++ "docs/**/bin"
    ++ "docs/**/obj"
    ++ "docs/**/build"
    ++ "docs/scss/extra"
    ++ "docs/public"
    |> Shell.CleanDirs
)

Target.create "YarnInstall"(fun _ ->
    yarn "install"
)

Target.create "DotnetRestore" (fun _ ->
    srcFiles
    |> Seq.iter (fun proj ->
        DotNet.restore id proj
))


let dotnet workingDir command args =
    DotNet.exec (fun p ->
                { p with WorkingDirectory = workingDir
                         DotNetCliPath = "dotnet" } )
        command
        args
    |> ignore

let build project framework =
    DotNet.build (fun p ->
        { p with Framework = Some framework } ) project

let mocha args =
    yarn (sprintf "run mocha %s" args)

Target.create "MochaTest" (fun _ ->
    !! testsGlob
    |> Seq.iter(fun proj ->
        let projDir = proj |> Path.getDirectory
        //Compile to JS
        dotnet projDir "fable" "yarn-run rollup --port free -- -c tests/rollup.config.js"

        //Run mocha tests
        let projDirOutput = projDir </> "bin"
        mocha projDirOutput
    )
)

let testNetFrameworkDir = "tests" </> "bin" </> "Release" </> "net461"
let testNetCoreDir = "tests" </> "bin" </> "Release" </> "netcoreapp2.0"

Target.create "ExpectoTest" (fun _ ->
    build "tests/Thot.Tests.fsproj" "netcoreapp2.0"
    build "tests/Thot.Tests.fsproj" "net461"

    mono testNetFrameworkDir "Thot.Tests.exe"
    dotnet testNetCoreDir "" "Thot.Tests.dll"
)

let root = __SOURCE_DIRECTORY__
let docs = root </> "docs"
let docsContent = docs </> "src" </> "Content"
let buildMain = docs </> "build" </> "src" </> "Main.js"

let execNPX args =
    Process.execSimple
        (fun info ->
            { info with
                FileName = "npx"
                Arguments = args
            }
        )
        (TimeSpan.FromSeconds 30.)
    |> ignore

let execNPXNoTimeout args =
    Process.execSimple
        (fun info ->
            { info with
                FileName = "npx"
                Arguments = args
            }
        )
        (TimeSpan.FromHours 2.)
    |> ignore

let buildSass _ =
    execNPX "node-sass --output-style compressed --output docs/public/ docs/scss/main.scss"

let applyAutoPrefixer _ =
    execNPX " postcss docs/public/main.css --use autoprefixer -o docs/public/main.css"

Target.create "Docs.Watch" (fun _ ->
    use watcher = new FileSystemWatcher(docsContent, "*.md")
    watcher.IncludeSubdirectories <- true
    watcher.EnableRaisingEvents <- true

    watcher.Changed.Add(fun _ ->
        Process.execSimple
            (fun info ->
                { info with
                    FileName = "node"
                    Arguments = buildMain }
            )
            (TimeSpan.FromSeconds 30.) |> ignore
    )

    // Make sure the style is generated
    // Watch mode of node-sass don't trigger a first build
    buildSass ()

    !! docFile
    |> Seq.iter (fun proj ->
        let projDir = proj |> Path.getDirectory

        [ async {
            dotnet projDir "fable" "yarn-run fable-splitter --port free -- -c docs/splitter.config.js -w"
          }
          async {
            execNPXNoTimeout "node-sass --output-style compressed --watch --output docs/public/ docs/scss/main.scss"
          }
        ]
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore
    )
)

Target.create "Docs.Setup" (fun _ ->
    // Make sure directories exist
    Directory.ensure "./docs/scss/extra/highlight.js/"

    // Copy files from node_modules allow us to manage them via yarn
    Shell.CopyDir "./docs/public/fonts" "./node_modules/font-awesome/fonts" (fun _ -> true)
    Shell.CopyFile "./docs/scss/extra/highlight.js/atom-one-light.css" "./node_modules/highlight.js/styles/atom-one-light.css"


    DotNet.restore id docFile
)

Target.create "Docs.Build" (fun _ ->
    !! docFile
    |> Seq.iter (fun proj ->
        let projDir = proj |> Path.getDirectory

        dotnet projDir "fable" "yarn-run fable-splitter --port free -- -c docs/splitter.config.js -p"
        buildSass ()
        applyAutoPrefixer ()
    )
)

Target.create "Watch" (fun _ ->
    !! testsGlob
    |> Seq.iter(fun proj ->
        let projDir = proj |> Path.getDirectory
        //Compile to JS
        dotnet projDir "fable" "yarn-run rollup --port free -- -c tests/rollup.config.js -w"
    )
)

let needsPublishing (versionRegex: Regex) (releaseNotes: ReleaseNotes.ReleaseNotes) projFile =
    printfn "Project: %s" projFile
    if releaseNotes.NugetVersion.ToUpper().EndsWith("NEXT")
    then
        Logger.warnfn "Version in Release Notes ends with NEXT, don't publish yet."
        false
    else
        File.ReadLines(projFile)
        |> Seq.tryPick (fun line ->
            let m = versionRegex.Match(line)
            if m.Success then Some m else None)
        |> function
            | None -> failwith "Couldn't find version in project file"
            | Some m ->
                let sameVersion = m.Groups.[1].Value = releaseNotes.NugetVersion
                if sameVersion then
                    Logger.warnfn "Already version %s, no need to publish." releaseNotes.NugetVersion
                not sameVersion

let pushNuget (releaseNotes: ReleaseNotes.ReleaseNotes) (projFile: string) =
    let versionRegex = Regex("<Version>(.*?)</Version>", RegexOptions.IgnoreCase)

    if needsPublishing versionRegex releaseNotes projFile then
        let projDir = Path.GetDirectoryName(projFile)
        let nugetKey =
            match Environment.environVarOrNone "NUGET_KEY" with
            | Some nugetKey -> nugetKey
            | None -> failwith "The Nuget API key must be set in a NUGET_KEY environmental variable"

        (versionRegex, projFile) ||> Util.replaceLines (fun line _ ->
            versionRegex.Replace(line, "<Version>" + releaseNotes.NugetVersion + "</Version>") |> Some)

        let pkgReleaseNotes = sprintf "/p:PackageReleaseNotes=\"%s\"" (String.toLines releaseNotes.Notes)

        DotNet.pack (fun p ->
            { p with
                Configuration = DotNet.Release
                Common = { p.Common with CustomParams = Some pkgReleaseNotes
                                         DotNetCliPath = "dotnet" } } )
            projFile

        Directory.GetFiles(projDir </> "bin" </> "Release", "*.nupkg")
        |> Array.find (fun nupkg -> nupkg.Contains(releaseNotes.NugetVersion))
        |> (fun nupkg ->
            (Path.GetFullPath nupkg, nugetKey)
            ||> sprintf "push %s -s nuget.org -k %s")
        |> dotnet "" "nuget"

Target.create "Publish" (fun _ ->
    srcFiles
    |> Seq.iter(fun s ->
        let projFile = s
        let projDir = IO.Path.GetDirectoryName(projFile)
        let release = projDir </> "RELEASE_NOTES.md" |> ReleaseNotes.load
        pushNuget release projFile
    )
)

// Where to push generated documentation
let githubLink = "git@github.com:MangelMaxime/thot.git"
let publishBranch = "gh-pages"
let repoRoot = __SOURCE_DIRECTORY__
let temp = repoRoot </> "temp"

Target.create "Docs.Publish" (fun _ ->
    // Clean the repo before cloning this avoid potential conflicts
    Shell.CleanDir temp
    Repository.cloneSingleBranch "" githubLink publishBranch temp

    // Copy new files
    Shell.CopyRecursive "docs/public" temp true |> printfn "%A"

    // Deploy the new site
    Staging.stageAll temp
    Commit.exec temp (sprintf "Update site (%s)" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")))
    Branches.push temp
)

"Clean"
    ==> "YarnInstall"
    ==> "DotnetRestore"
    ==> "MochaTest"
    ==> "ExpectoTest"
    ==> "Publish"

"Docs.Build"
    <== [ "Docs.Setup" ]

"Docs.Watch"
    <== [ "Docs.Setup" ]

"Docs.Build"
    ==> "Docs.Publish"

Target.runOrDefault "ExpectoTest"
