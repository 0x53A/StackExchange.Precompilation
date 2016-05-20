[<AutoOpen>]
module Hydra.Program

open System
open System.Threading
open System.Reflection
open System.Diagnostics
open System.Threading.Tasks
open Microsoft.Win32

open Nessos.Thespian
open System.IO


let internal launchFileHashActorAsSeperateProcess() = async {
    use receiver = Receiver.create<ActorRef<FileHashActor.ServerMsg>> () |> Actor.Publish
    let! awaiter = receiver.ReceiveEvent |> Async.AwaitEvent |> Async.StartChild
    
    let exe = Assembly.GetExecutingAssembly().Location
    let base64Receiver = VagabondConfig.Serializer.Pickle receiver.Ref |> Convert.ToBase64String
    use proc = Process.Start(exe, "--file-hash-cache " + base64Receiver)

    let! serverRef = awaiter
    return serverRef
}

let internal getOrLaunchFileHashActor() = async {
    // aquire mutex, to make sure there are no simultanious process launches
    use m = new Mutex(false, "Local\\Hydra")
    m.WaitOne() |> ignore

    // try to read value from registry
    use rk = Registry.CurrentUser.CreateSubKey("Software\\Hydra")
    let! existingActor = async {
        let b64existingActorRef = rk.GetValue("FileHashActorRef") :?> string
        if b64existingActorRef <> null then
            try
                let ref = VagabondConfig.Serializer.UnPickle<ActorRef<FileHashActor.ServerMsg>> (Convert.FromBase64String b64existingActorRef)
                do! ref.PostWithReply (fun ch -> FileHashActor.ServerMsg.Ping ch)
                return Some ref
            with
                exn -> return None
        else
            return None
    }

    match existingActor with
    | Some ref -> return ref
    | None ->
        // no actor exists -> launch and set in registry
        let! ref = launchFileHashActorAsSeperateProcess()
        let b64ref = ref |> VagabondConfig.Serializer.Pickle |> Convert.ToBase64String
        rk.SetValue("FileHashActorRef", b64ref)
        return ref
}

// C# Interface
type IFileHashActorRef =    
     abstract member GetHashOfFileAsync : string -> Task<Guid>
     abstract member GetHashOfFile : string -> Guid

let getHashActorRef() = Async.StartAsTask( async {
    let! actorRef = getOrLaunchFileHashActor()
    let post path = actorRef.PostWithReply((fun ch -> FileHashActor.ServerMsg.GetHashOf(path, ch)), timeout = 1*1000)
    return { new IFileHashActorRef with
                 member x.GetHashOfFileAsync path =
                    post path |> Async.StartAsTask
                 member x.GetHashOfFile path =
                    post path |> Async.RunSynchronously
           }
  })


type Cache() =
    interface StackExchange.Precompilation.ICompilationCache with
        member x.TryEmit(hashKey, outputPath, pdbPath, documentationPath, diagnostics) = failwith "nyi"
        member x.CalculateHash(commandLine, cscArgs, compilationModules) = failwith "nyi"
        member x.Cache(hashKey, outputPath, pdbPath, documentationPath, diagnostics) = failwith "nyi"


[<EntryPoint>]
let main argv =
    printfn "Starting: %A" argv
    match argv with
    | Array [|"--file-hash-cache"|] [|x64|] ->
        let bytes = Convert.FromBase64String x64
        let ref = VagabondConfig.Serializer.UnPickle<ActorRef<ActorRef<FileHashActor.ServerMsg>>> bytes
        let mutable isAlive = true
        let a = FileHashActor.get (fun () -> isAlive <- false)
        let actor = Actor.Publish(a, name= "hydra")
        ref.Post actor.Ref
        while isAlive do Thread.Sleep 1000
        0
    | Array [|"--attach-worker"|] tail ->
        0
    | _ ->
        let asx = async {
            let! ref = launchFileHashActorAsSeperateProcess()
            do! ref.PostWithReply(fun ch -> FileHashActor.ServerMsg.Ping ch)
            let file = Path.GetTempFileName()
            File.WriteAllText(file, "this is a test!")
            let! hash = ref.PostWithReply(fun ch -> FileHashActor.ServerMsg.GetHashOf (file, ch))
            return hash
        }
        let hash = asx |> Async.RunSynchronously
        eprintfn "Error: unexpected args: %A" argv
        exit 0
