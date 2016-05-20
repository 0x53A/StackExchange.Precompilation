module Hydra.FileHashActor

open System
open System.IO
open System.Collections.Generic
open System.Collections.Concurrent
open System.Security.Cryptography

open Nessos.Thespian

type ServerMsg =
    | WatchDirectory of string
    | GetHashOf of string * IReplyChannel<Guid>
    | Ping of IReplyChannel<unit>

type internal Change = string * WatcherChangeTypes

let get (shutdown:unit->unit) =
    // closure
    let cachedHashes = ConcurrentDictionary<string, Guid>()
    let changed = ConcurrentQueue<Change>()
    let fsws = List<FileSystemWatcher>()
    // serverLoop
    let rec serverLoop (self : Actor<ServerMsg>) = async {
        let! msg = self.TryReceive(timeout=5*60*1000)
        match msg with
        | None ->
            shutdown()
            self.Stop()
            return ()
        | Some msg ->
            match msg with
            | Ping ch -> do! ch.Reply ()
            | WatchDirectory dir ->
                let fsw = new FileSystemWatcher(dir)
                fsw.NotifyFilter <- NotifyFilters.FileName |||
                                    NotifyFilters.DirectoryName |||
                                    NotifyFilters.LastWrite |||
                                    NotifyFilters.Size
                fsw.Changed.Add(fun x -> changed.Enqueue(x.FullPath,x.ChangeType))
                fsw.Error.Add(fun x -> cachedHashes.Clear())
                fsw.InternalBufferSize <- 64 * 1024
                fsw.EnableRaisingEvents <- true
                fsws.Add(fsw)
            | GetHashOf (ids, rc) ->
                try
                    let hash =
                        let hash = ref Guid.Empty
                        match cachedHashes.TryGetValue(ids, hash) with
                        | true -> hash.Value
                        | false ->
                            use md5 = MD5.Create()
                            File.OpenRead ids |> md5.ComputeHash |> Guid
                    do! rc.Reply hash
                with
                | exn -> do! rc.ReplyWithException exn
            return! serverLoop self
    }
    serverLoop |> Actor.bind