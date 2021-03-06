open System
open System.IO

#r @"packages/FAKE/tools/FakeLib.dll"
open Fake
open Fake.Git
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper

#if MONO
#else
#load "packages/SourceLink.Fake/tools/Fake.fsx"
open SourceLink
#endif

// --------------------------------------------------------------------------------------
// START TODO: Provide project-specific details below
// --------------------------------------------------------------------------------------

// Information about the project are used
//  - for version and project name in generated AssemblyInfo file
//  - by the generated NuGet package
//  - to run tests and to publish documentation on GitHub gh-pages
//  - for documentation, you also need to edit info in "docs/tools/generate.fsx"

// The name of the project
// (used by attributes in AssemblyInfo, name of a NuGet package and directory in 'src')
let project = "Exira.AwsS3Cache"

// Short summary of the project
// (used as description in AssemblyInfo and as a short summary for NuGet package)
let summary = "Exira.AwsS3Cache is an IIS module which reads cached objects from AWS S3 and quickly serves them to end users"

// Longer description of the project
// (used as a description for NuGet package; line breaks are automatically cleaned up)
let description = "Exira.AwsS3Cache is an IIS module which reads cached objects from AWS S3 and quickly serves them to end users"

// List of author names (for NuGet package)
let authors = [ "exira" ]

// Tags for your project (for NuGet package)
let tags = "aws s3 f#"

// File system information
let solutionFile  = "aws-s3-cache.sln"

// Pattern specifying assemblies to be tested using NUnit
let testAssemblies = "tests/**/bin/Release/*Tests*.dll"

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted
let gitOwner = "exira"
let gitHome = "git@github.com:" + gitOwner

// The name of the project on GitHub
let gitName = "aws-s3-cache"

// The url for the raw files hosted
let gitRaw = environVarOrDefault "gitRaw" "https://raw.github.com/exira"

// --------------------------------------------------------------------------------------
// END TODO: The rest of the file includes standard build steps
// --------------------------------------------------------------------------------------

// Read additional information from the release notes document
let release = LoadReleaseNotes "RELEASE_NOTES.md"

// Helper active pattern for project types
let (|Fsproj|Csproj|Vbproj|) (projFileName:string) =
    match projFileName with
    | f when f.EndsWith("fsproj") -> Fsproj
    | f when f.EndsWith("csproj") -> Csproj
    | f when f.EndsWith("vbproj") -> Vbproj
    | _                           -> failwith (sprintf "Project file %s not supported. Unknown project type." projFileName)

let buildNumber =
    match environVar "BUILD_NUMBER" with
    | ""
    | null -> "0"
    | _ as nr -> nr

let commitHash =
    match environVar "BUILD_VCS_NUMBER" with
    | ""
    | null -> Information.getCurrentSHA1(".")
    | _ as sha -> sha

// Generate assembly info files with the right version & up-to-date information
Target "AssemblyInfo" (fun _ ->
    log (sprintf "##teamcity[buildNumber '%s.%s']" release.AssemblyVersion buildNumber)

    let getAssemblyInfoAttributes projectName =
        [ Attribute.Title (projectName)
          Attribute.Product project
          Attribute.Description summary
          Attribute.Version (sprintf "%s.%s" release.AssemblyVersion buildNumber)
          Attribute.FileVersion (sprintf "%s.%s" release.AssemblyVersion buildNumber)
          Attribute.Metadata("githash", commitHash) ]

    let getProjectDetails projectPath =
        let projectName = System.IO.Path.GetFileNameWithoutExtension(projectPath)
        ( projectPath,
          projectName,
          System.IO.Path.GetDirectoryName(projectPath),
          (getAssemblyInfoAttributes projectName)
        )

    !! "src/**/*.??proj"
    |> Seq.map getProjectDetails
    |> Seq.iter (fun (projFileName, projectName, folderName, attributes) ->
        match projFileName with
        | Fsproj -> CreateFSharpAssemblyInfo (folderName @@ "AssemblyInfo.fs") attributes
        | Csproj -> CreateCSharpAssemblyInfo ((folderName @@ "Properties") @@ "AssemblyInfo.cs") attributes
        | Vbproj -> CreateVisualBasicAssemblyInfo ((folderName @@ "My Project") @@ "AssemblyInfo.vb") attributes
        )
)

// Copies binaries from default VS location to exepcted bin folder
// But keeps a subdirectory structure for each project in the
// src folder to support multiple project outputs
Target "CopyBinaries" (fun _ ->
    !! "src/**/*.??proj"
    |>  Seq.map (fun f -> ((System.IO.Path.GetDirectoryName f) @@ "bin/Release", "bin" @@ (System.IO.Path.GetFileNameWithoutExtension f)))
    |>  Seq.iter (fun (fromDir, toDir) -> CopyDir toDir fromDir (fun _ -> true))
)

// --------------------------------------------------------------------------------------
// Clean build results

Target "Clean" (fun _ ->
    CleanDirs ["bin"; "temp"]
)

Target "CleanDocs" (fun _ ->
    CleanDirs ["docs/output"]
)

// --------------------------------------------------------------------------------------
// Build library & test project

Target "Build" (fun _ ->
    log "##teamcity[progressStart 'Build']"

    !! solutionFile
    |> MSBuildRelease "" "Rebuild"
    |> ignore

    log "##teamcity[progressFinish 'Build']"
)

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner

Target "RunTests" (fun _ ->
    !! testAssemblies
    |> NUnit (fun p ->
        { p with
            DisableShadowCopy = true
            TimeOut = TimeSpan.FromMinutes 20.
            OutputFile = "TestResults.xml" })
)

#if MONO
#else
// --------------------------------------------------------------------------------------
// SourceLink allows Source Indexing on the PDB generated by the compiler, this allows
// the ability to step through the source code of external libraries https://github.com/ctaggart/SourceLink

Target "SourceLink" (fun _ ->
    let baseUrl = sprintf "%s/%s/{0}/%%var2%%" gitRaw (gitName.ToLower())
    use repo = new GitRepo(__SOURCE_DIRECTORY__)

    let addAssemblyInfo (projFileName:String) =
        match projFileName with
        | Fsproj -> (projFileName, "**/AssemblyInfo.fs")
        | Csproj -> (projFileName, "**/AssemblyInfo.cs")
        | Vbproj -> (projFileName, "**/AssemblyInfo.vb")

    !! "src/**/*.??proj"
    |> Seq.map addAssemblyInfo
    |> Seq.iter (fun (projFile, assemblyInfo) ->
        let proj = VsProj.LoadRelease projFile
        logfn "source linking %s" proj.OutputFilePdb
        let files = proj.Compiles -- assemblyInfo
        repo.VerifyChecksums files
        proj.VerifyPdbChecksums files
        proj.CreateSrcSrv baseUrl repo.Revision (repo.Paths files)
        Pdbstr.exec proj.OutputFilePdb proj.OutputFilePdbSrcSrv
    )
)

#endif

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target "NuGet" (fun _ ->
    log "##teamcity[progressStart 'NuGet']"

    Paket.Pack(fun p ->
        { p with
            OutputPath = "bin"
            Version = (sprintf "%s.%s" release.NugetVersion buildNumber)
            ReleaseNotes = toLines release.Notes})

    log "##teamcity[progressFinish 'NuGet']"
)

Target "PublishNuget" (fun _ ->
    ()
    //Paket.Push(fun p ->
    //    { p with
    //        WorkingDir = "bin" })
)


// --------------------------------------------------------------------------------------
// Generate the documentation

Target "GenerateReferenceDocs" (fun _ ->
    if not <| executeFSIWithArgs "docs/tools" "generate.fsx" ["--define:RELEASE"; "--define:REFERENCE"] [] then
      failwith "generating reference documentation failed"
)

let generateHelp' fail debug =
    let args =
        if debug then ["--define:HELP"]
        else ["--define:RELEASE"; "--define:HELP"]
    if executeFSIWithArgs "docs/tools" "generate.fsx" args [] then
        traceImportant "Help generated"
    else
        if fail then
            failwith "generating help documentation failed"
        else
            traceImportant "generating help documentation failed"

let generateHelp fail =
    generateHelp' fail false

Target "GenerateHelp" (fun _ ->
    DeleteFile "docs/content/release-notes.md"
    CopyFile "docs/content/" "RELEASE_NOTES.md"
    Rename "docs/content/release-notes.md" "docs/content/RELEASE_NOTES.md"

    DeleteFile "docs/content/license.md"
    CopyFile "docs/content/" "LICENSE.txt"
    Rename "docs/content/license.md" "docs/content/LICENSE.txt"

    generateHelp true
)

Target "GenerateHelpDebug" (fun _ ->
    DeleteFile "docs/content/release-notes.md"
    CopyFile "docs/content/" "RELEASE_NOTES.md"
    Rename "docs/content/release-notes.md" "docs/content/RELEASE_NOTES.md"

    DeleteFile "docs/content/license.md"
    CopyFile "docs/content/" "LICENSE.txt"
    Rename "docs/content/license.md" "docs/content/LICENSE.txt"

    generateHelp' true true
)

Target "KeepRunning" (fun _ ->
    use watcher = new FileSystemWatcher(DirectoryInfo("docs/content").FullName,"*.*")
    watcher.EnableRaisingEvents <- true
    watcher.Changed.Add(fun e -> generateHelp false)
    watcher.Created.Add(fun e -> generateHelp false)
    watcher.Renamed.Add(fun e -> generateHelp false)
    watcher.Deleted.Add(fun e -> generateHelp false)

    traceImportant "Waiting for help edits. Press any key to stop."

    System.Console.ReadKey() |> ignore

    watcher.EnableRaisingEvents <- false
    watcher.Dispose()
)

Target "GenerateDocs" DoNothing

let createIndexFsx lang =
    let content = """(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use
// it to define helpers that you do not want to show in the documentation.
#I "../../../bin"

(**
F# Project Scaffold ({0})
=========================
*)
"""
    let targetDir = "docs/content" @@ lang
    let targetFile = targetDir @@ "index.fsx"
    ensureDirectory targetDir
    System.IO.File.WriteAllText(targetFile, System.String.Format(content, lang))

Target "AddLangDocs" (fun _ ->
    let args = System.Environment.GetCommandLineArgs()
    if args.Length < 4 then
        failwith "Language not specified."

    args.[3..]
    |> Seq.iter (fun lang ->
        if lang.Length <> 2 && lang.Length <> 3 then
            failwithf "Language must be 2 or 3 characters (ex. 'de', 'fr', 'ja', 'gsw', etc.): %s" lang

        let templateFileName = "template.cshtml"
        let templateDir = "docs/tools/templates"
        let langTemplateDir = templateDir @@ lang
        let langTemplateFileName = langTemplateDir @@ templateFileName

        if System.IO.File.Exists(langTemplateFileName) then
            failwithf "Documents for specified language '%s' have already been added." lang

        ensureDirectory langTemplateDir
        Copy langTemplateDir [ templateDir @@ templateFileName ]

        createIndexFsx lang)
)

// --------------------------------------------------------------------------------------
// Release Scripts

Target "ReleaseDocs" (fun _ ->
    let tempDocsDir = "temp/gh-pages"
    CleanDir tempDocsDir
    Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") "gh-pages" tempDocsDir

    ensureDirectory "docs/output"
    CopyRecursive "docs/output" tempDocsDir true |> tracefn "%A"
    StageAll tempDocsDir
    Git.Commit.Commit tempDocsDir (sprintf "Update generated documentation for version %s.%s" release.NugetVersion buildNumber)
    Branches.push tempDocsDir
)

#load "paket-files/fsharp/FAKE/modules/Octokit/Octokit.fsx"
open Octokit

Target "Release" (fun _ ->
    log "##teamcity[progressStart 'Release']"

    StageAll ""
    Git.Commit.Commit "" (sprintf "Bump version to %s.%s" release.NugetVersion buildNumber)
    Branches.push ""

    Branches.tag "" (sprintf "%s.%s" release.NugetVersion buildNumber)
    Branches.pushTag "" "origin" (sprintf "%s.%s" release.NugetVersion buildNumber)

    // release on github
    createClient (getBuildParamOrDefault "github-user" "") (getBuildParamOrDefault "github-pw" "")
    |> createDraft gitOwner gitName (sprintf "%s.%s" release.NugetVersion buildNumber) (release.SemVer.PreRelease <> None) release.Notes
    // TODO: |> uploadFile "PATH_TO_FILE"
    |> releaseDraft
    |> Async.RunSynchronously

    log "##teamcity[progressFinish 'Release']"
)

Target "BuildPackage" DoNothing

// --------------------------------------------------------------------------------------
// Run all targets by default. Invoke 'build <Target>' to override

Target "All" DoNothing

"Clean"
  ==> "AssemblyInfo"
  ==> "Build"
  ==> "CopyBinaries"
  //==> "RunTests"
  ==> "GenerateReferenceDocs"
  ==> "GenerateDocs"
  ==> "All"
  ==> "ReleaseDocs"

"All"
#if MONO
#else
  =?> ("SourceLink", Pdbstr.tryFind().IsSome )
#endif
  ==> "NuGet"
  ==> "BuildPackage"

"CleanDocs"
  ==> "GenerateHelp"
  ==> "GenerateReferenceDocs"
  ==> "GenerateDocs"

"CleanDocs"
  ==> "GenerateHelpDebug"

"GenerateHelp"
  ==> "KeepRunning"

"ReleaseDocs"
  ==> "Release"

"BuildPackage"
  ==> "PublishNuget"
  ==> "Release"

RunTargetOrDefault "All"
