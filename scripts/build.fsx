#r @"../packages/FAKE/tools/FakeLib.dll"
#r @"../packages/FParsec/lib/netstandard1.6/FParsecCS.dll"
#r @"../packages/FParsec/lib/netstandard1.6/FParsec.dll"
#load @"../src/app/Steinpilz.DevFlow.Fake.Lib/env.fs"
#load @"../src/app/Steinpilz.DevFlow.Fake.Lib/buildParams.fs"
#load @"../src/app/Steinpilz.DevFlow.Fake.Lib/buildUtils.fs"
#load @"../src/app/Steinpilz.DevFlow.Fake.Lib/pub.fs"
#load @"../src/app/Steinpilz.DevFlow.Fake.Lib/lib.fs"

open Fake
open Steinpilz.DevFlow.Fake

let param = Lib.setup <| fun p ->
    { p with
        AppProjects = !!"src/**/*.fsproj"
        PublishProjects = !! "src/**/*.fsproj"
        UseNuGetToPack = true
        NuGetFeed =
            { p.NuGetFeed with
                ApiKey = environVarOrFail <| "NUGET_API_KEY" |> Some
            }
    }

let packTool param version =
    CreateDir param.PublishDir
    NuGetPack(fun p ->
    { p with
        Version = version
        WorkingDir = param.ArtifactsDir
        OutputPath = param.PublishDir
        Files =
            [
                (@"build/**/*", Some "tools", None)
            ]
    }) ("src/app/Steinpilz.DevFlow.Fake/Steinpilz.DevFlow.Fake.nuspec" |> FullName)

Target "Pack-Tool" (fun _ ->
    let param = param()
    let v = (param.VersionPrefix, param.VersionSuffix) ||> fullVersion
    packTool param v
)

"Build"
    ==> "Pack-Tool"
    ==> "Pack"
    |> ignore

RunTargetOrDefault "Watch"
