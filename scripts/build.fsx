// include Fake lib
#r @"..\packages\FAKE\tools\FakeLib.dll"

#load @"..\src\app\Steinpilz.DevFlow.Fake.Lib\lib.fs"

open Steinpilz.DevFlow.Fake
open Fake

let param = Lib.setup <| fun p -> 
    { p with 
        AppProjects = !!"src/**/*.fsproj"
        TestProjects = !!"test/**/*.fsproj"
        PublishProjects = !! "src/**/*.fsproj"
        UseNuGetToPack = true
        UseNuGetToRestore = true
        NuGetFeed = 
            { p.NuGetFeed with 
                ApiKey = environVarOrFail <| "NUGET_API_KEY" |> Some
            }
    }

let packTool version =
    CreateDir param.PublishDir
    NuGetPack(fun p ->
    {p with
        Version = version
        WorkingDir = param.ArtifactsDir
        OutputPath = param.PublishDir
        Files = 
            [ 
                (@"build\**\*", Some "tools", None) 
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
