#r @"../packages/FAKE/tools/FakeLib.dll"
#load @"../src/app/Steinpilz.DevFlow.Fake.Lib/env.fs"
#load @"../src/app/Steinpilz.DevFlow.Fake.Lib/pub.fs"
#load @"../src/app/Steinpilz.DevFlow.Fake.Lib/lib.fs"

open Fake
open Steinpilz.DevFlow.Fake

let param = Lib.setup <| fun p ->
    { p with
        AppProjects = !!"src/**/*.fsproj"
        PublishProjects = !! "src/**/*.fsproj"

        NuGetFeed =
            { p.NuGetFeed with
                ApiKey = environVarOrFail <| "NUGET_API_KEY" |> Some
            }
    }

let packTool version =
    CreateDir param.PublishDir
    NuGetPack(fun p ->
    { p with
        Version = version
        WorkingDir = param.ArtifactsDir
        OutputPath = param.PublishDir
        Files =
            [
                (@"build\**\*", Some "tools", None)
            ]
    }) ("src/app/Steinpilz.DevFlow.Fake/Steinpilz.DevFlow.Fake.nuspec" |> FullName)

Target "Pack-Tool" (fun _ ->
    let vp = param.VersionPrefix |> Option.defaultValue ""
    let vs = param.VersionSuffix |> Option.map ((+) "-") |> Option.defaultValue ""
    packTool <| vp + vs
)

"Build"
    ==> "Pack-Tool"
    ==> "Pack"
    |> ignore

RunTargetOrDefault "Watch"
