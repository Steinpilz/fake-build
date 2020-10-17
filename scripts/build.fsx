#r "paket:
framework: netstandard20
nuget Fake.DotNet.Cli
nuget Fake.Core.Target 
nuget Fake.Core.Environment
nuget Fake.Core.Target
nuget Fake.Core.Trace
nuget Fake.DotNet.AssemblyInfoFile
nuget Fake.DotNet.MsBuild
nuget Fake.IO.FileSystem
nuget FSharp.Collections.ParallelSeq
nuget FSharp.Core //"

#load @"../src/app/Steinpilz.DevFlow.Fake5/lib.fs"

open Fake.Core
open Fake.IO.Globbing.Operators
open Steinpilz.DevFlow.Fake

let param = Lib.setup <| fun p -> 
    { p with 
        AppProjects = !!"src/**/*.fsproj"
        TestProjects = !!"test/**/*.fsproj"
        PublishProjects = !! "src/**/*.fsproj"
        NuGetFeed = 
            { p.NuGetFeed with 
                ApiKey = Environment.environVarOrFail <| "NUGET_API_KEY" |> Some
            }
    }

Target.runOrDefault "Pack"
