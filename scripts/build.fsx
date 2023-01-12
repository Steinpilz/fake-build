// include Fake lib
#r @"../packages/FAKE/tools/FakeLib.dll"
#r @"../packages/FSharp.Collections.ParallelSeq/lib/netstandard2.0/FSharp.Collections.ParallelSeq.dll"
#r @"../src/app/Steinpilz.DevFlow.Fake.Lib/bin/Debug/net462/Steinpilz.DevFlow.Fake.Lib.dll"

open Steinpilz.DevFlow.Fake
open Fake

let param = Lib.setup <| fun p ->
    { p with 
        AppProjects = !!"src/**/*.fsproj"
        TestProjects = !!"test/**/*.fsproj"
        PublishProjects = !! "src/**/*.fsproj"
        UseDotNetCliToBuild = true
        UseDotNetCliToPack = true
        UseDotNetCliToRestore = true
        NuGetFeed = 
            { p.NuGetFeed with 
                ApiKey = environVarOrFail <| "NUGET_API_KEY" |> Some
            }
        IncludeSymbols = false
        EmbedSources = false
    }

let packTool version =
    CreateDir param.PublishDir
    NuGetPack(fun p ->
    {p with
        Version = version
        WorkingDir = param.ArtifactsDir @@ "build"
        OutputPath = param.PublishDir
        Files = 
            [ 
                (@"**/*", Some "tools", None) 
            ]
    }) ("src/app/Steinpilz.DevFlow.Fake/Steinpilz.DevFlow.Fake.nuspec" |> FullName)    

Target "Pack-Tool" (fun _ -> 
    let vs = match param.VersionSuffix with
                 | null | "" -> ""
                 | s -> "-" + s
    packTool <| param.VersionPrefix + vs
)

"Build"
    ==> "Pack-Tool"
    ==> "Pack"
    |> ignore

RunTargetOrDefault "Watch"
