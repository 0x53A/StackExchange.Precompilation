module Hydra

open System

open Microsoft.CodeAnalysis.CSharp

open Nessos.FsPickler
open Nessos.Thespian
open System.Threading
open Nessos.Thespian.Remote.TcpProtocol

open Nessos.Thespian
open Nessos.Thespian.Serialization
open Nessos.Thespian.Remote
open Nessos.Thespian.Remote.TcpProtocol
open System.IO
open Nessos.Vagabond
open System.Reflection
open System.Diagnostics
open System.Threading.Tasks
open System.Collections.Generic
open System.Collections.Concurrent

type Cache() =
    interface StackExchange.Precompilation.ICompilationCache with
        member x.TryEmit(hashKey, outputPath, pdbPath, documentationPath, diagnostics) : bool = failwith "nyi"
        member x.CalculateHash(commandLine, cscArgs, compilationModules) = failwith "nyi"
        member x.Cache(hashKey, outputPath, pdbPath, documentationPath, diagnostics) = failwith "nyi"

let compareSequences = Seq.compareWith Operators.compare
let expectedSameAsResult expected result = (compareSequences expected result = 0)
let (|Array|_|) pattern toMatch =
    let patternLength = Seq.length pattern
    let toMatchLength = Array.length toMatch
    if patternLength > toMatchLength then
        None
    else
        if toMatch |> Seq.take patternLength |> expectedSameAsResult pattern then
            let tail = toMatch.[patternLength..]
            Some (tail)
        else
            None


type internal ServerMsg =
    | WatchDirectory of string
    | GetHashOf of string * IReplyChannel<Guid>


/// Vagabond configuration container
type VagabondConfig private () =

    static let manager =
        let cachePath = Path.Combine(Path.GetTempPath(), sprintf "thunkServerCache-%s" <| Guid.NewGuid().ToString("N"))
        let _ = Directory.CreateDirectory cachePath
        Vagabond.Initialize(cacheDirectory = cachePath, ignoredAssemblies = [Assembly.GetExecutingAssembly()])

    static member Instance = manager
    static member Serializer = manager.Pickler
        
        
/// Actor configuration tools
type Actor private () =

    static do
        let _ = System.Threading.ThreadPool.SetMinThreads(100, 100) 
        defaultSerializer <- new FsPicklerMessageSerializer(VagabondConfig.Serializer)
        Nessos.Thespian.Default.ReplyReceiveTimeout <- Timeout.Infinite
        TcpListenerPool.RegisterListener(IPEndPoint.any)

    /// Publishes an actor instance to the default TCP protocol
    static member Publish(actor : Actor<'T>, ?name) =
        let name = match name with Some n -> n | None -> Guid.NewGuid().ToString()
        actor
        |> Actor.rename name
        |> Actor.publish [ Protocols.utcp() ]
        |> Actor.start
        
    /// Publishes an actor instance to the default TCP protocol
    static member Publish(receiver : Receiver<'T>, ?name) =
        let name = match name with Some n -> n | None -> Guid.NewGuid().ToString()
        receiver
        |> Receiver.rename name
        |> Receiver.publish [ Protocols.utcp() ]
        |> Receiver.start

    static member EndPoint = TcpListenerPool.GetListener().LocalEndPoint
type Change = string * WatcherChangeTypes
let changed = ConcurrentQueue<Change>()
let fsws = List<FileSystemWatcher>()
let rec internal serverLoop (self : Actor<ServerMsg>) = async {
    let! msg = self.Receive()

    match msg with
    | WatchDirectory dir ->
        let fsw = new FileSystemWatcher(dir)
        fsw.NotifyFilter <- NotifyFilters.FileName |||
                            NotifyFilters.DirectoryName |||
                            NotifyFilters.LastWrite |||
                            NotifyFilters.Size
        fsw.Changed.Add(fun x -> changed.Enqueue(x.FullPath,x.ChangeType))
        fsw.InternalBufferSize <- max fsw.InternalBufferSize (64*1024)
        fsw.EnableRaisingEvents <- true
        fsws.Add(fsw)
        ()
    | GetHashOf (ids, rc) ->
        let replies = Guid.Empty
        do! rc.Reply replies

    return! serverLoop self
}

let internal launch() = async {
    use receiver = Receiver.create<ActorRef<ServerMsg>> () |> Actor.Publish
    let! awaiter = receiver.ReceiveEvent |> Async.AwaitEvent |> Async.StartChild
    
    let exe = Assembly.GetExecutingAssembly().Location
    let argument = VagabondConfig.Serializer.Pickle receiver.Ref |> System.Convert.ToBase64String
    let proc = Process.Start(exe, argument)

    let! serverRef = awaiter
    return serverRef
}

let internal getActorRef() =
    let wasCreated = ref false
    use m = new Mutex(false, "Local\\Hydra", wasCreated)
    m.WaitOne() |> ignore
    let p = Process.GetProcessesByName("Hydra")
    m.ReleaseMutex()
    launch()

type IFileHashActorRef =    
     abstract member GetHashOf : string -> Task<Guid>

let getHashActorRef() = Async.StartAsTask( async {
    let! actorRef = getActorRef()

    return { new IFileHashActorRef with
                 member x.GetHashOf path = actorRef.PostWithReply(fun ch -> GetHashOf(path, ch)) |> Async.StartAsTask }
  })
[<EntryPoint>]
let main argv =
    match argv with
    | Array ["--file-hash-cache"] [|x64|] ->
        let bytes = Convert.FromBase64String x64
        let ref = VagabondConfig.Serializer.UnPickle<ActorRef<ActorRef<ServerMsg>>> bytes        
        let actor = serverLoop |> Actor.bind |> (fun a-> Actor.Publish(a, name= "hydra"))
        ref.Post actor.Ref
        while true do Thread.Sleep 1000
        0
    | Array ["--attach-worker"] tail ->
        0
    | _ ->
        eprintfn "Error: unexpected args: %A" argv
        exit 1 
