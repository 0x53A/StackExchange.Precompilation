[<AutoOpen>]
module Hydra.Config

open System
open System.IO
open System.Reflection

open Nessos.Vagabond

/// Vagabond configuration container
type VagabondConfig private () =

    static let manager =
        let cachePath = Path.Combine(Path.GetTempPath(), sprintf "thunkServerCache-%s" <| Guid.NewGuid().ToString("N"))
        let _ = Directory.CreateDirectory cachePath
        Vagabond.Initialize(cacheDirectory = cachePath, ignoredAssemblies = [Assembly.GetExecutingAssembly()])

    static member Instance = manager
    static member Serializer = manager.Pickler
       