module Steinpilz.DevFlow.Fake.Lib
open Fake
open BuildParams
open BuildUtils

let setup setParams =
    buildParams <- setParams buildParams

    // Targets
    Target "Clean" (fun _ ->
        clean buildParams
    )

    Target "Restore" (fun _ ->
        restore buildParams
    )

    Target "Build" (fun _ ->
        build buildParams
    )

    Target "Test" (fun _ ->
        runTests buildParams
    )

    Target "Watch" (fun _ ->
        watch buildParams
    )

    Target "Pack" (fun _ ->
        packProjects buildParams buildParams.PublishProjects buildParams.VersionPrefix buildParams.VersionSuffix
    )

    Target "Pack-Pre" (fun _ ->
        packProjects buildParams buildParams.PublishProjects buildParams.VersionPrefix (buildParams.VersionSuffix |> Option.defaultValue "no-version" |> Some)
    )

    Target "Publish" <| fun _ ->
        publish buildParams

    Target "Publish-Release" (fun _ ->
        publish buildParams
    )

    Target "Publish-Pre" (fun _ ->
        publish buildParams
    )

    Target "Default" <| DoNothing

    Pub.setup id

    // Dependencies
    "Clean"
        ==> "Restore"
        ==> "Build"
        ==> "Default"
        |> ignore

    "Restore"
        ==> "Test"
        |> ignore

    "Clean"
        ==> "Restore"
        ==> "Pack-Pre"
        |> ignore

    "Clean"
        ==> "Restore"
        ==> "Pack"
        |> ignore

    "Pack"
        ==> "Publish-Release"
        |> ignore

    "Pack"
        ==> "Publish"
        |> ignore

    "Pack-Pre"
        ==> "Publish-Pre"
        |> ignore

    fun _ -> buildParams
