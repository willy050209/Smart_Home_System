open System
open System.Net.Http
open System.Threading
open System.Diagnostics

let defaultUrl = "http://127.0.0.1:8080" 
let concurrencyLevel = 1000 

type StatsMessage =
    | AddSuccess
    | AddError
    | Reset
    | Get of AsyncReplyChannel<int * int>

let statsActor = MailboxProcessor.Start(fun inbox ->
    let rec loop success error = async {
        let! msg = inbox.Receive()
        match msg with
        | AddSuccess -> return! loop (success + 1) error
        | AddError -> return! loop success (error + 1)
        | Reset -> return! loop 0 0
        | Get reply -> 
            reply.Reply(success, error)
            return! loop success error
    }
    loop 0 0
)

let handler = new SocketsHttpHandler(
    PooledConnectionLifetime = TimeSpan.FromMinutes(10.0),
    MaxConnectionsPerServer = 1000 
)
let client = new HttpClient(handler)


let attackHeavy (baseUrl: string) = async {
    try
        let! response = client.GetByteArrayAsync(baseUrl + "/api/camera/frame") |> Async.AwaitTask
        if response.Length > 0 then statsActor.Post AddSuccess
        else statsActor.Post AddError
    with _ -> 
        statsActor.Post AddError
}

let attackFast (baseUrl: string) = async {
    try
        let! response = client.GetAsync(baseUrl + "/api/hw/system-status") |> Async.AwaitTask
        if response.IsSuccessStatusCode then statsActor.Post AddSuccess
        else statsActor.Post AddError
    with _ -> 
        statsActor.Post AddError
}

let rec workerLoop (baseUrl: string) (mode: string) = async {
    if mode = "HEAVY" then
        do! attackHeavy baseUrl
    else
        do! attackFast baseUrl
    
    return! workerLoop baseUrl mode
}

// --- 主程式 ---
[<EntryPoint>]
let main argv =
    Console.ForegroundColor <- ConsoleColor.Cyan
    printfn "=================================================="
    printfn "   TX2 Smart Home Load Tester (F# Power)   "
    printfn "=================================================="
    Console.ResetColor()

    printf "Enter TX2 Server URL (Default: %s): " defaultUrl
    let inputUrl = Console.ReadLine()
    let targetUrl = if String.IsNullOrWhiteSpace(inputUrl) then defaultUrl else inputUrl.Trim().TrimEnd('/')

    printfn "\nTargeting: %s" targetUrl
    printfn "Launching %d concurrent workers..." concurrencyLevel
    printfn "Press [Ctrl+C] to stop.\n"

    let monitor = async {
        while true do
            do! Async.Sleep 1000
            let! (s, e) = statsActor.PostAndAsyncReply Get
            statsActor.Post Reset 
            
            Console.Write("\r[Load Test] RPS: ")
            Console.ForegroundColor <- ConsoleColor.Green
            Console.Write($"{s,5} req/s")
            Console.ResetColor()
            Console.Write(" | Errors: ")
            if e > 0 then Console.ForegroundColor <- ConsoleColor.Red
            Console.Write($"{e,3}")
            Console.ResetColor()
            Console.Write(" | Status: Bombarding TX2...  ")
    }

    let heavyWorkersCount = int (float concurrencyLevel * 0.3)
    let fastWorkersCount = concurrencyLevel - heavyWorkersCount

    let heavyTasks = 
        [1 .. heavyWorkersCount] 
        |> List.map (fun _ -> workerLoop targetUrl "HEAVY")

    let fastTasks = 
        [1 .. fastWorkersCount] 
        |> List.map (fun _ -> workerLoop targetUrl "FAST")

    let allTasks = 
        [monitor] @ heavyTasks @ fastTasks
        |> Async.Parallel

    try
        Async.RunSynchronously allTasks |> ignore
        0
    with
    | :? OperationCanceledException -> 0
    | ex -> 
        printfn "Error: %s" ex.Message
        1