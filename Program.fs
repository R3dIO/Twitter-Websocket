module Main

open System
open System.Collections.Generic
open Newtonsoft.Json
open Akka.Actor
open Akka.Configuration
open Akka.FSharp

open Suave
open Suave.Http
open Suave.Filters
open Suave.Operators
open Suave.Successful
open Suave.ServerErrors
open Suave.Writers
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Suave.Files
open Suave.RequestErrors
open Suave.Logging
open Suave.Utils

open Datatype

type LiveUserHandlerMsg =
    | SelfTweet of WebSocket * NewTweet
    | SendTweet of WebSocket * NewTweet
    | Following of WebSocket * string
    | SendMention of WebSocket * NewTweet

let system = ActorSystem.Create("TwitterEngine")

let setCORSHeaders =
    setHeader  "Access-Control-Allow-Origin" "*"
    >=> setHeader "Access-Control-Allow-Headers" "content-type"

let mutable usersMap = Map.empty
let mutable websockMap = Map.empty
let mutable activeUsersMap = Map.empty
let mutable mentionsMap = Map.empty
let mutable hashTagsMap = Map.empty
let mutable tweetOwnerMap = Map.empty
let mutable followersMap = Map.empty   

let byteResponseToWSRes (message:string) =
    message
    |> System.Text.Encoding.ASCII.GetBytes
    |> ByteSegment

let getString (rawForm: byte[]) =
    System.Text.Encoding.UTF8.GetString(rawForm)

let getJsonObject<'a> json =
    JsonConvert.DeserializeObject(json, typeof<'a>) :?> 'a

let checkUserExistance username = usersMap.TryFind(username) <> None
let isOnline username = activeUsersMap.TryFind(username) <> None

let getResponseMessage (comment, content, status, errorStatus) =
    let response = {Comment = comment; Content=content; status=status; error=errorStatus}
    response

let isUserLoggedIn username = 
    if activeUsersMap.TryFind(username) <> None then // User is Logged In
        (getResponseMessage($"", [], 0, true), true) 
    else 
        if usersMap.TryFind(username) = None then  // User Doesn't Exsist
            (getResponseMessage($"User Doesn't Exsist!!Please Register", [], 0, true), false)
        else // User Exsists but not logged in         
            (getResponseMessage($"Please Login", [], 1, true), false) 

let addUser (user: Register) =
    if not (checkUserExistance(user.UserName)) then
        usersMap <- usersMap.Add(user.UserName,user.Password)
        getResponseMessage("User Registerd Succesfully", [], 1, false)
    else
        getResponseMessage("User Already Registerd", [], 1, true)

let loginuser (user: Login) = 
    printfn $"Received Login Request from {user.UserName} as {user}"  
    if not (checkUserExistance(user.UserName)) then
        getResponseMessage("User not found. Please Register", [], 0, true)
    else
        let userObj = usersMap.TryFind(user.UserName)
        if userObj.Value.CompareTo(user.Password) = 0 then
            if not (isOnline(user.UserName)) then
                activeUsersMap <- activeUsersMap.Add(user.UserName,true)
                getResponseMessage("Logged In Succesfully", [], 2, false)
            else
                getResponseMessage("Already Logged In", [], 2, true)
        else
            getResponseMessage("Incorrect Password", [], 1, true)

let logoutuser (user:Logout) = 
    printfn $"Received Logout Request from {user.UserName} as {user}"  
    if not (checkUserExistance(user.UserName)) then
        getResponseMessage("User not found; Please Register ", [], 0, true)
    else
        if not (isOnline(user.UserName)) then
            getResponseMessage("User Not Logged In; Please Log In", [], 1, true)
        else
            activeUsersMap <- activeUsersMap.Remove(user.UserName)
            getResponseMessage("User Logged out Succesfully", [], 1, false)

let liveUserActor (mailbox:Actor<_>) = 
    let rec loop() = actor{
        let! msg = mailbox.Receive()
        let mutable response = ""
        match msg with
        |SelfTweet(ws,tweet)->  
                    let response = $"You have tweeted '{tweet.Tweet}'"
                    let byteResponse = byteResponseToWSRes response 
                    Async.StartAsTask (socket { do! ws.send Text byteResponse true }) |> ignore
        |SendTweet(ws,tweet)->
                    let response = $"{tweet.UserName} has sent tweet '{tweet.Tweet}'"
                    let byteResponse = byteResponseToWSRes response
                    Async.StartAsTask (socket { do! ws.send Text byteResponse true }) |> ignore
        |SendMention(ws, tweet)->
                    let response = $"{tweet.UserName} mentioned you in tweet '{tweet.Tweet}'"
                    let byteResponse = byteResponseToWSRes response
                    Async.StartAsTask (socket { do! ws.send Text byteResponse true }) |> ignore
        |Following(ws,msg)->
                    let response = msg
                    let byteResponse = byteResponseToWSRes response
                    Async.StartAsTask (socket { do! ws.send Text byteResponse true }) |> ignore
        return! loop()
    }
    loop()
let liveUserActorRef = spawn system "luar" liveUserActor

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
                    websockMap <- websockMap.Add(username,webSocket)
                    printfn $"Connected to {username} websocket"
                else
                    let response = $"Response to {strData}"
                    let byteResponse = byteResponseToWSRes response
                    do! webSocket.send Text byteResponse true
              | (Close, _, _) ->
                let emptyResponse = [||] |> ByteSegment
                do! webSocket.send Close emptyResponse true
                loop <- false
              | _ -> ()
    }

let tweetParser (tweet:NewTweet) =
    let splits = (tweet.Tweet.Split ' ')
    for word in splits do
        if word.StartsWith "@" then
            let mentions = (word.Split '@')
            let user = mentions.[1]
            if checkUserExistance user then
                let mentionList = mentionsMap.TryFind(user)
                if  mentionList = None then
                    let mutable userMentionMapLocal = Map.empty
                    let tweetlist = new List<string>()
                    tweetlist.Add(tweet.Tweet)
                    userMentionMapLocal <- userMentionMapLocal.Add(tweet.UserName, tweetlist)
                    mentionsMap <- mentionsMap.Add(user, userMentionMapLocal)
                else
                    let mention = mentionList.Value.TryFind(tweet.UserName)
                    if mention = None then
                        let tweetlist = new List<string>()
                        tweetlist.Add(tweet.Tweet)
                        let mutable userMentionMapLocal = mentionList.Value
                        userMentionMapLocal <- userMentionMapLocal.Add(tweet.UserName, tweetlist)
                        mentionsMap <- mentionsMap.Add(user,userMentionMapLocal)
                    else
                        mention.Value.Add(tweet.Tweet)
                let userWSCon = websockMap.TryFind(user)
                if userWSCon <> None then
                    liveUserActorRef <! SendMention(userWSCon.Value, tweet)
        elif word.StartsWith "#" then
            let hashtags = word.Split '#'
            let hashtagList = hashTagsMap.TryFind(hashtags.[1])
            if hashtagList = None then
                let lstTweet = List<string>()
                lstTweet.Add(tweet.Tweet)
                hashTagsMap <- hashTagsMap.Add(hashtags.[1],lstTweet)
            else
                hashtagList.Value.Add(tweet.Tweet)


let addFollower (follower: Follower) =
    printfn "Received Follower Request from %s as %A" follower.UserName follower
    let (resp, status) = isUserLoggedIn follower.UserName
    if status then
        if (checkUserExistance follower.Following) then
            let followersList = followersMap.TryFind(follower.Following)
            let userWSCon = websockMap.TryFind(follower.UserName)
            if followersList = None then
                let lstTweet = new List<string>()
                lstTweet.Add(follower.UserName)
                followersMap <- followersMap.Add(follower.Following, lstTweet)
                if userWSCon <> None then
                    liveUserActorRef <! Following(userWSCon.Value,$"You are now following: {follower.Following}")
                getResponseMessage("Sucessfully Added to the Following list", [], 2, false)
            else
                if followersList.Value.Exists( fun x -> x.CompareTo(follower.UserName) = 0 ) then
                    if userWSCon <> None then
                        liveUserActorRef <! Following(userWSCon.Value,$"You are already following: {follower.Following}")
                    getResponseMessage($"You are already Following {follower.Following}", [], 2, true)
                else
                    followersList.Value.Add(follower.UserName)
                    if userWSCon <> None then
                        liveUserActorRef <! Following(userWSCon.Value,$"You are now following: {follower.Following}")
                    getResponseMessage($"Sucessfully Added to the Following list", [], 2, false)
        else
            getResponseMessage($"Follower {follower.Following} doesn't exsist", [], 2, true)
    else
        resp

let addTweet (tweet: NewTweet) =
    let owner = tweetOwnerMap.TryFind(tweet.UserName)
    if owner = None then
        let lstTweet = new List<string>()
        lstTweet.Add(tweet.Tweet)
        tweetOwnerMap <- tweetOwnerMap.Add(tweet.UserName,lstTweet)
    else
        owner.Value.Add(tweet.Tweet)

let addTweetToFollowers (tweet: NewTweet) = 
    let followersList = followersMap.TryFind(tweet.UserName)
    if followersList <> None then
        for follower in followersList.Value do
            let tweet = {Tweet=tweet.Tweet; UserName=follower}
            addTweet tweet
            let followerWSCon = websockMap.TryFind(follower)
            printfn $"Found follower online {follower}"
            if followerWSCon <> None then
                liveUserActorRef <! SendTweet(followerWSCon.Value,tweet)

let tweetActor (mailbox:Actor<_>) =
    let rec loop() = actor{
        let! msg = mailbox.Receive()
        match msg with 
        | AddTweetMsg(tweet) -> addTweet(tweet)
                                let userWSCon = websockMap.TryFind(tweet.UserName)
                                if userWSCon <> None then
                                    liveUserActorRef <! SelfTweet(userWSCon.Value,tweet)
        | AddTweetToFollowersMsg(tweet) ->  addTweetToFollowers(tweet)
        | TweetParserMsg(tweet) -> tweetParser(tweet)
        return! loop()
    }
    loop()

let tweetActorRef = spawn system "thar" tweetActor

let addTweetToUser (tweet: NewTweet) =
    let (resp,status) = isUserLoggedIn tweet.UserName
    if status then
        tweetActorRef <! AddTweetMsg(tweet) // addTweet tweet
        tweetActorRef <! AddTweetToFollowersMsg(tweet) // addTweetToFollowers tweet
        tweetActorRef <! TweetParserMsg(tweet) // tweetParser tweet
        getResponseMessage($"Tweeted Succesfully", [], 2, false)
    else
        resp

let getTweets username =
    let (resp,status) = isUserLoggedIn username
    if status then
        let userTweets = tweetOwnerMap.TryFind(username)
        if userTweets = None then
            getResponseMessage($"No Tweets", [], 2, false)
        else
            let len = Math.Min(10,userTweets.Value.Count)
            let res = [for i in 1 .. len do yield(userTweets.Value.[i-1])]
            getResponseMessage($"Get Tweets done Succesfully", res, 2, false)
    else
        resp

let getMentions username = 
    let (resp,status) = isUserLoggedIn username
    if status then
        let mentionList = mentionsMap.TryFind(username)
        if mentionList = None then
            getResponseMessage($"No Mentions", [], 2, false)
        else
            let mentionSubList = new List<string>()
            for mention in mentionList.Value do
                for tweet in mention.Value do
                    mentionSubList.Add(tweet)
            let len = Math.Min(10,mentionSubList.Count)
            let result = [for i in 1 .. len do yield(mentionSubList.[i-1])]
            getResponseMessage($"Get Mentions done Succesfully", result, 2, false)
    else
        resp

let getHashTags username hashtag =
    let resp, status = isUserLoggedIn username
    if status then
        printf $"Request to serach for Hashtag {hashtag}"
        let hashTagList = hashTagsMap.TryFind(hashtag)
        if hashTagList = None then
            getResponseMessage($"No Tweets with this hashtag found", [], 2, false)
        else
            let len = Math.Min(10,hashTagList.Value.Count)
            let res = [for i in 1 .. len do yield(hashTagList.Value.[i-1])] 
            getResponseMessage($"Get Hashtags done Succesfully", res, 2, false)
    else
        resp

let registerNewUser (user: Register) =
    printfn "Received Register Request from %s as %A" user.UserName user
    addUser user

let newTweetUser (tweet: NewTweet) =
    printfn "Received Tweet Request from %s as %A" tweet.UserName tweet
    addTweetToUser tweet

let setHeaders rspMsg =
    rspMsg
    |> JsonConvert.SerializeObject
    |> OK
    >=> setMimeType "application/json"
    >=> setCORSHeaders

let getUserTweets username =
    printfn "Received GetTweets Request from %s " username
    getTweets username
    |> setHeaders 

let getUserMentions username =
    printfn "Received GetMentions Request from %s " username
    getMentions username
    |> setHeaders 
 
let getUserHashtags username hashtag =
    printfn "Received GetHashTag Request from %s for hashtag %A" username hashtag
    getHashTags username hashtag
    |> setHeaders

let postResponse (operation) =
    let subresponse input = 
        match operation with 
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
                |> newTweetUser
        | "Follow" -> 
                input 
                |> getJsonObject<Follower>
                |> addFollower 
        | _ -> getResponseMessage("Invalid Operation", [], 0, true)

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
                path "/home" >=> file "client.html"; browseHome 
                pathScan "/search/gettweets/%s" (fun username -> (getUserTweets username))
                pathScan "/search/getmentions/%s" (fun username -> (getUserMentions username))
                pathScan "/search/gethashtags/%s/%s" (fun (username,hashtag) -> (getUserHashtags username hashtag))
                ]

            POST >=> choose
                [   
                path "/register" >=> (fun context -> context |> postResponse("Register"))
                path "/login" >=> (fun context -> context |> postResponse("Login"))
                path "/logout" >=> (fun context -> context |> postResponse("Logout"))
                path "/newtweet" >=> (fun context -> context |> postResponse("NewTweet")) 
                path "/follow" >=> (fun context -> context |> postResponse("Follow"))
              ]

            NOT_FOUND "404 - No page found."
        ]

[<EntryPoint>]
let main argv =
    startWebServer defaultConfig app
    0
