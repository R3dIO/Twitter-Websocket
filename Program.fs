module Main

open System
open System.Collections.Generic
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful
open Suave.ServerErrors
open Suave.Writers
open Newtonsoft.Json
open Akka.Actor
open Akka.Configuration
open Akka.FSharp

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Datatype

type LiveUserHandlerMsg =
    | SendTweet of WebSocket * NewTweet
    | SendMention of WebSocket * NewTweet
    | SelfTweet of WebSocket * NewTweet
    | Following of WebSocket * string
let system = ActorSystem.Create("TwitterEngine")

let setCORSHeaders =
    setHeader  "Access-Control-Allow-Origin" "*"
    >=> setHeader "Access-Control-Allow-Headers" "content-type"

let mutable users = Map.empty
let mutable activeUsers = Map.empty
let mutable tweetOwner = Map.empty
let mutable followers = Map.empty
let mutable mentions = Map.empty
let mutable hashTags = Map.empty
let mutable websockmap = Map.empty

let buildByteResponseToWS (message:string) =
    message
    |> System.Text.Encoding.ASCII.GetBytes
    |> ByteSegment

let getString (rawForm: byte[]) =
    System.Text.Encoding.UTF8.GetString(rawForm)

let getJsonObject<'a> json =
    JsonConvert.DeserializeObject(json, typeof<'a>) :?> 'a

let checkUserExistance username = users.TryFind(username) <> None
let isOnline username = activeUsers.TryFind(username) <> None

let getResponseMessage (comment, content, status, errorStatus) =
    let response = {Comment = comment; Content=content; status=status; error=errorStatus}
    response

let isUserLoggedIn username = 
    if activeUsers.TryFind(username) <> None then // User is Logged In
        (getResponseMessage($"", [], 0, true), true) 
    else 
        if users.TryFind(username) = None then  // User Doesn't Exsist
            (getResponseMessage($"User Doesn't Exsist!!Please Register", [], 0, true), false)
        else // User Exsists but not logged in         
            (getResponseMessage($"Please Login", [], 1, true), false) 

let addUser (user: Register) =
    if not (checkUserExistance(user.UserName)) then
        users <- users.Add(user.UserName,user.Password)
        getResponseMessage("User Registerd Succesfully", [], 1, false)
    else
        getResponseMessage("User Already Registerd", [], 1, true)

let loginuser (user: Login) = 
    printfn $"Received Login Request from {user.UserName} as {user}"  
    if not (checkUserExistance(user.UserName)) then
        getResponseMessage("User not found. Please Register", [], 0, true)
    else
        let userObj =  users.TryFind(user.UserName)
        if userObj.Value.CompareTo(user.Password) = 0 then
            if not (isOnline(user.UserName)) then
                activeUsers <- activeUsers.Add(user.UserName,true)
                getResponseMessage("Logged In Succesfully", [], 2, false)
            else
                getResponseMessage("Already Logged In", [], 2, true)
        else
            getResponseMessage("Incorrect Password", [], 1, true)

let logoutuser (user:Logout) = 
    printfn $"Received Logout Request from {user.UserName} as {user}"  
    let temp = users.TryFind(user.UserName)
    if not (checkUserExistance(user.UserName)) then
        getResponseMessage("User not found; Please Register ", [], 0, true)
    else
        if not (isOnline(user.UserName)) then
            getResponseMessage("User Not Logged In; Please Log In", [], 1, true)
        else
            activeUsers <- activeUsers.Remove(user.UserName)
            getResponseMessage("User Logged out Succesfully", [], 1, false)

let liveUserHandler (mailbox:Actor<_>) = 
    let rec loop() = actor{
        let! msg = mailbox.Receive()
        let mutable response = ""
        match msg with
        |SelfTweet(ws,tweet)->  let response = $"You have tweeted '{tweet.Tweet}'"
                                let byteResponse = buildByteResponseToWS response
                                let s = socket { do! ws.send Text byteResponse true }
                                Async.StartAsTask s |> ignore
        |SendTweet(ws,tweet)->
                                let response = $"{tweet.UserName} has tweeted '{tweet.Tweet}'"
                                let byteResponse = buildByteResponseToWS response
                                let s = socket{ do! ws.send Text byteResponse true}
                                Async.StartAsTask s |> ignore
        |SendMention(ws,tweet)->
                                let response = $"{tweet.UserName} mentioned you in tweet '{tweet.Tweet}'"
                                let byteResponse = buildByteResponseToWS response
                                let s = socket{ do! ws.send Text byteResponse true}
                                Async.StartAsTask s |> ignore
        |Following(ws,msg)->
                                let response = msg
                                let byteResponse = buildByteResponseToWS response
                                let s = socket{ do! ws.send Text byteResponse true}
                                Async.StartAsTask s |> ignore
        return! loop()
    }
    loop()
let liveUserHandlerRef = spawn system "luref" liveUserHandler

let websocketHandler (webSocket : WebSocket) (context: HttpContext) =
    socket {
        let mutable loop = true

        while loop do
              let! msg = webSocket.read()

              match msg with
              | (Text, data, true) ->
                let strData = UTF8.toString data 
                if strData.StartsWith("UserName:") then
                    let username = strData.Split(':').[1]
                    websockmap <- websockmap.Add(username,webSocket)
                    printfn $"connected to {username} websocket"
                else
                    let response = $"Response to {strData}"
                    let byteResponse = buildByteResponseToWS response
                    do! webSocket.send Text byteResponse true
              | (Close, _, _) ->
                let emptyResponse = [||] |> ByteSegment
                do! webSocket.send Close emptyResponse true
                loop <- false
              | _ -> ()
    }

let tweetParser (tweet:NewTweet) =
    let splits = (tweet.Tweet.Split ' ')
    for i in splits do
        if i.StartsWith "@" then
            let temp = i.Split '@'
            if checkUserExistance temp.[1] then
                let temp1 = mentions.TryFind(temp.[1])
                if temp1 = None then
                    let mutable mp = Map.empty
                    let tlist = new List<string>()
                    tlist.Add(tweet.Tweet)
                    mp <- mp.Add(tweet.UserName,tlist)
                    mentions <- mentions.Add(temp.[1],mp)
                else
                    let temp2 = temp1.Value.TryFind(tweet.UserName)
                    if temp2 = None then
                        let tlist = new List<string>()
                        tlist.Add(tweet.Tweet)
                        let mutable mp = temp1.Value
                        mp <- mp.Add(tweet.UserName,tlist)
                        mentions <- mentions.Add(temp.[1],mp)
                    else
                        temp2.Value.Add(tweet.Tweet)
                let temp3 = websockmap.TryFind(temp.[1])
                if temp3<>None then
                    liveUserHandlerRef <! SendMention(temp3.Value,tweet)
        elif i.StartsWith "#" then
            let temp1 = i.Split '#'
            let temp = hashTags.TryFind(temp1.[1])
            if temp = None then
                let lst = List<string>()
                lst.Add(tweet.Tweet)
                hashTags <- hashTags.Add(temp1.[1],lst)
            else
                temp.Value.Add(tweet.Tweet)


let addFollower (follower: Follower) =
    printfn "Received Follower Request from %s as %A" follower.UserName follower
    let (resp, status) = isUserLoggedIn follower.UserName
    if status then
        if (checkUserExistance follower.Following) then
            let temp = followers.TryFind(follower.Following)
            let temp1 = websockmap.TryFind(follower.UserName)
            if temp = None then
                let lst = new List<string>()
                lst.Add(follower.UserName)
                followers <- followers.Add(follower.Following,lst)
                if temp1 <> None then
                    liveUserHandlerRef <! Following(temp1.Value,$"You are now following: {follower.Following}")
                getResponseMessage("Sucessfully Added to the Following list", [], 2, false)
            else
                if temp.Value.Exists( fun x -> x.CompareTo(follower.UserName) = 0 ) then
                    if temp1 <> None then
                        liveUserHandlerRef <! Following(temp1.Value,$"You are already following: {follower.Following}")
                    getResponseMessage($"You are already Following {follower.Following}", [], 2, true)
                else
                    temp.Value.Add(follower.UserName)
                    if temp1 <> None then
                        liveUserHandlerRef <! Following(temp1.Value,$"You are now following: {follower.Following}")
                    getResponseMessage($"Sucessfully Added to the Following list", [], 2, false)
        else
            getResponseMessage($"Follower {follower.Following} doesn't exsist", [], 2, true)
    else
        resp

let addTweet (tweet: NewTweet) =
    let temp = tweetOwner.TryFind(tweet.UserName)
    if temp = None then
        let lst = new List<string>()
        lst.Add(tweet.Tweet)
        tweetOwner <- tweetOwner.Add(tweet.UserName,lst)
    else
        temp.Value.Add(tweet.Tweet)
    

let addTweetToFollowers (tweet: NewTweet) = 
    let temp = followers.TryFind(tweet.UserName)
    if temp <> None then
        for i in temp.Value do
            let temp1 = {Tweet=tweet.Tweet;UserName=i}
            addTweet temp1
            let temp2 = websockmap.TryFind(i)
            printfn "%s" i
            if temp2 <> None then
                liveUserHandlerRef <! SendTweet(temp2.Value,tweet)

let tweetHandler (mailbox:Actor<_>) =
    let rec loop() = actor{
        let! msg = mailbox.Receive()
        match msg with 
        | AddTweetMsg(tweet) -> addTweet(tweet)
                                let temp = websockmap.TryFind(tweet.UserName)
                                if temp <> None then
                                    liveUserHandlerRef <! SelfTweet(temp.Value,tweet)
        | AddTweetToFollowersMsg(tweet) ->  addTweetToFollowers(tweet)
        | TweetParserMsg(tweet) -> tweetParser(tweet)
        return! loop()
    }
    loop()

let tweetHandlerRef = spawn system "thref" tweetHandler

let addTweetToUser (tweet: NewTweet) =
    let (resp,status) = isUserLoggedIn tweet.UserName
    if status then
        tweetHandlerRef <! AddTweetMsg(tweet) // addTweet tweet
        tweetHandlerRef <! AddTweetToFollowersMsg(tweet) // addTweetToFollowers tweet
        tweetHandlerRef <! TweetParserMsg(tweet) // tweetParser tweet
        getResponseMessage($"Tweeted Succesfully", [], 2, false)
    else
        resp

let getTweets username =
    let (resp,status) = isUserLoggedIn username
    if status then
        let temp = tweetOwner.TryFind(username)
        if temp = None then
            getResponseMessage($"No Tweets", [], 2, false)
        else
            let len = Math.Min(10,temp.Value.Count)
            let res = [for i in 1 .. len do yield(temp.Value.[i-1])]
            getResponseMessage($"Get Tweets done Succesfully", res, 2, false)
    else
        resp

let getMentions username = 
    let (resp,status) = isUserLoggedIn username
    if status then
        let temp = mentions.TryFind(username)
        if temp = None then
            getResponseMessage($"No Mentions", [], 2, false)
        else
            let res = new List<string>()
            for i in temp.Value do
                for j in i.Value do
                    res.Add(j)
            let len = Math.Min(10,res.Count)
            let res1 = [for i in 1 .. len do yield(res.[i-1])]
            getResponseMessage($"Get Mentions done Succesfully", res1, 2, false)
    else
        resp

let getHashTags username hashtag =
    let resp, status = isUserLoggedIn username
    if status then
        printf "%s" hashtag
        let temp = hashTags.TryFind(hashtag)
        if temp = None then
            getResponseMessage($"No Tweets with this hashtag found", [], 2, false)
        else
            let len = Math.Min(10,temp.Value.Count)
            let res = [for i in 1 .. len do yield(temp.Value.[i-1])] 
            getResponseMessage($"Get Hashtags done Succesfully", res, 2, false)
    else
        resp

let registerNewUser (user: Register) =
    printfn "Received Register Request from %s as %A" user.UserName user
    addUser user

let respTweet (tweet: NewTweet) =
    printfn "Received Tweet Request from %s as %A" tweet.UserName tweet
    addTweetToUser tweet

//bridge functions between routes

let gettweets username =
    printfn "Received GetTweets Request from %s " username
    getTweets username
    |> JsonConvert.SerializeObject
    |> OK
    >=> setMimeType "application/json"
    >=> setCORSHeaders

let getmentions username =
    printfn "Received GetMentions Request from %s " username
    getMentions username
    |> JsonConvert.SerializeObject
    |> OK
    >=> setMimeType "application/json"
    >=> setCORSHeaders

let gethashtags username hashtag =
    printfn "Received GetHashTag Request from %s for hashtag %A" username hashtag
    getHashTags username hashtag
    |> JsonConvert.SerializeObject
    |> OK
    >=> setMimeType "application/json"
    >=> setCORSHeaders

let postResponse (operataion) =
    let subresponse input = 
        match operataion with 
        | "Register" -> 
                input 
                |> getJsonObject<Register>
                |> registerNewUser
        | "Login" -> 
                input 
                |> getJsonObject<Login>
                |> loginuser
        | "Logout" -> 
                input 
                |> getJsonObject<Logout>
                |> logoutuser
        | "NewTweet" -> 
                input 
                |> getJsonObject<NewTweet>
                |> respTweet
        | "Follow" -> 
                input 
                |> getJsonObject<Follower>
                |> addFollower 

    request (fun context ->
    context.rawForm
    |> getString
    |> subresponse
    |> JsonConvert.SerializeObject
    |> OK
    )
    >=> setMimeType "application/json"
    >=> setCORSHeaders


let allow_cors : WebPart =
    choose [
        OPTIONS >=>
            fun context ->
                context |> (
                    setCORSHeaders
                    >=> OK "CORS approved" )
    ]

//setup app routes
let app =
    choose
        [ 
            path "/websocket" >=> handShake websocketHandler 
            allow_cors
            GET >=> choose
                [ 
                path "/" >=> OK "Server is up and Running" 
                pathScan "/gettweets/%s" (fun username -> (gettweets username))
                pathScan "/getmentions/%s" (fun username -> (getmentions username))
                pathScan "/gethashtags/%s/%s" (fun (username,hashtag) -> (gethashtags username hashtag))
                ]

            POST >=> choose
                [   
                path "/register" >=> (fun context -> context |> postResponse("Register"))
                path "/login" >=> (fun context -> context |> postResponse("Login"))
                path "/logout" >=> (fun context -> context |> postResponse("Logout"))
                path "/newtweet" >=> (fun context -> context |> postResponse("NewTweet")) 
                path "/follow" >=> (fun context -> context |> postResponse("Follow"))
              ]

        ]

[<EntryPoint>]
let main argv =
    startWebServer defaultConfig app
    0
