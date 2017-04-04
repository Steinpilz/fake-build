// include Fake lib
#r @"..\packages\FAKE\tools\FakeLib.dll"

#load @"..\src\app\Steinpilz.DevFlow.Fake\lib.fs"

open Steinpilz.DevFlow.Fake
open Fake

Lib.setup <| fun p -> 
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
