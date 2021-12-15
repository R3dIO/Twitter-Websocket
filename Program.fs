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

type WsLiveUserActorMsg =
    | SendTweetNotification of WebSocket * NewTweet
    | RecieveTweetNotification of WebSocket * NewTweet
    | MentionTweetNotification of WebSocket * NewTweet
    | FollowingTweetNotification of WebSocket * string 

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

let liveUserActor (mailbox:Actor<_>) = 
    let rec loop() = actor{
        let! msg = mailbox.Receive()
        match msg with
        | SendTweetNotification (ws,tweet)->  
                    let response = $"You tweeted '{tweet.Tweet}'"
                    Async.StartAsTask (socket { do! ws.send Text (byteResponseToWSRes response) true }) |> ignore
        | RecieveTweetNotification (ws,tweet)->
                    let response = $"{tweet.UserName} sent tweet '{tweet.Tweet}'"
                    Async.StartAsTask (socket { do! ws.send Text (byteResponseToWSRes response) true }) |> ignore
        | MentionTweetNotification (ws, tweet)->
                    let response = $"{tweet.UserName} tagged you in tweet '{tweet.Tweet}'"
                    Async.StartAsTask (socket { do! ws.send Text (byteResponseToWSRes response) true }) |> ignore
        | FollowingTweetNotification (ws,message)->
                    Async.StartAsTask (socket { do! ws.send Text (byteResponseToWSRes message) true }) |> ignore
        return! loop()
    }
    loop()
let liveUserActorRef = spawn system "liveuser" liveUserActor

let isUserLoggedIn username = 
    if activeUsersMap.TryFind(username) <> None then 
        (getResponseMessage($"", [], 0, true), true) 
    else 
        if usersMap.TryFind(username) = None then
            (getResponseMessage($"User does not exists, please register!", [], 0, true), false)
        else          
            (getResponseMessage($"User exists, please login!", [], 1, true), false) 

let UserRegister (user: Register) =
    printfn $"Register request received:{user.UserName} as {user}" 
    if not (checkUserExistance(user.UserName)) then
        usersMap <- usersMap.Add(user.UserName,user.Password)
        getResponseMessage("User has been registered successfully", [], 1, false)
    else
        getResponseMessage("User exists, please login!", [], 1, true)

let UserLogin (user: Login) = 
    printfn $"Login request received: User: {user.UserName} Password: {user.Password}"  
    if not (checkUserExistance(user.UserName)) then
        getResponseMessage("User does not exists, please register!", [], 0, true)
    else
        let userObj = usersMap.TryFind(user.UserName)
        if userObj.Value.CompareTo(user.Password) = 0 then
            if not (isOnline(user.UserName)) then
                activeUsersMap <- activeUsersMap.Add(user.UserName,true)
                getResponseMessage("Login successful", [], 2, false)
            else
                getResponseMessage("Already logged in", [], 2, true)
        else
            getResponseMessage("Please enter correct password!", [], 1, true)

let UserLogout (user:Logout) = 
    printfn $"Logout request received: User: {user.UserName}"  
    if not (checkUserExistance(user.UserName)) then
        getResponseMessage("User does not exists!", [], 0, true)
    else
        if not (isOnline(user.UserName)) then
            getResponseMessage("Please login to continue!", [], 1, true)
        else
            activeUsersMap <- activeUsersMap.Remove(user.UserName)
            getResponseMessage("Logout successful", [], 1, false)

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
                    printfn $"{username} connected to websocket"
                else
                    let response = $"Response: {strData}"
                    do! webSocket.send Text (byteResponseToWSRes response) true

              | (Close, _, _) ->
                let emptyResponse = [||] |> ByteSegment
                do! webSocket.send Close emptyResponse true
                loop <- false

              | _ -> ()
    }

let ParseTweet (tweet:NewTweet) =
    let splits = (tweet.Tweet.Split ' ')
    for word in splits do
        if word.StartsWith "#" then
            let hashtags = word.Split '#'
            let hashtagList = hashTagsMap.TryFind(hashtags.[1])
            if hashtagList = None then
                let lstTweet = List<string>()
                lstTweet.Add(tweet.Tweet)
                hashTagsMap <- hashTagsMap.Add(hashtags.[1],lstTweet)
            else
                hashtagList.Value.Add(tweet.Tweet)
        
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
                    liveUserActorRef <! MentionTweetNotification(userWSCon.Value, tweet)
        


let UpdateFollowers (follower: Follower) =
    printfn "Follower request received: %s as %A" follower.UserName follower
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
                    liveUserActorRef <! FollowingTweetNotification(userWSCon.Value,$"You are folloing: {follower.Following}")
                getResponseMessage("Added to follow list", [], 2, false)
            else
                if followersList.Value.Exists( fun followerInst -> followerInst.CompareTo(follower.UserName) = 0 ) then
                    if userWSCon <> None then
                        liveUserActorRef <! FollowingTweetNotification(userWSCon.Value,$"You already follow: {follower.Following}")
                    getResponseMessage($"You already follow: {follower.Following}", [], 2, true)
                else
                    followersList.Value.Add(follower.UserName)
                    if userWSCon <> None then
                        liveUserActorRef <! FollowingTweetNotification(userWSCon.Value,$"You are now following: {follower.Following}")
                    getResponseMessage($"Added to follow list", [], 2, false)
        else
            getResponseMessage($"User {follower.Following} doesn't exist!", [], 2, true)
    else
        resp

let UpdateTweets (tweet: NewTweet) =
    let owner = tweetOwnerMap.TryFind(tweet.UserName)
    if owner = None then
        let lstTweet = new List<string>()
        lstTweet.Add(tweet.Tweet)
        tweetOwnerMap <- tweetOwnerMap.Add(tweet.UserName,lstTweet)
    else
        owner.Value.Add(tweet.Tweet)

let SendTweetToFollowers (tweet: NewTweet) = 
    let followersList = followersMap.TryFind(tweet.UserName)
    if followersList <> None then
        for follower in followersList.Value do
            let tweet = {Tweet=tweet.Tweet; UserName=tweet.UserName}
            UpdateTweets tweet
            let followerWSCon = websockMap.TryFind(follower)
            printfn $"{follower} follower is online"
            if followerWSCon <> None then
                liveUserActorRef <! RecieveTweetNotification(followerWSCon.Value,tweet)

let tweetActor (mailbox:Actor<_>) =
    let rec loop() = actor{
        let! msg = mailbox.Receive()
        match msg with 
        | AddTweetMsg(tweet) -> 
                UpdateTweets(tweet)
                let userWSCon = websockMap.TryFind(tweet.UserName)
                if userWSCon <> None then
                    liveUserActorRef <! SendTweetNotification(userWSCon.Value,tweet)
        | AddTweetToFollowersMsg(tweet) ->  SendTweetToFollowers(tweet)
        | TweetParserMsg(tweet) -> ParseTweet(tweet)
        return! loop()
    }
    loop()

let tweetActorRef = spawn system "tweetuser" tweetActor

let addTweetToUser (tweet: NewTweet) =
    printfn $"Tweet request received: {tweet.UserName} as {tweet}" 
    let (resp,status) = isUserLoggedIn tweet.UserName
    if status then
        tweetActorRef <! AddTweetMsg(tweet) 
        tweetActorRef <! AddTweetToFollowersMsg(tweet) 
        tweetActorRef <! TweetParserMsg(tweet) 
        getResponseMessage($"Tweet successful", [], 2, false)
    else
        resp

let getTweetsList username =
    let (resp,status) = isUserLoggedIn username
    if status then
        let userTweets = tweetOwnerMap.TryFind(username)
        if userTweets = None then
            getResponseMessage($"No tweets found", [], 2, false)
        else
            let res = [for item in 1 .. (Math.Min(5 ,userTweets.Value.Count)) do yield(userTweets.Value.[item-1])]
            getResponseMessage($"Tweets get done", res, 2, false)
    else
        resp

let getHashTagsList username hashtag =
    let resp, status = isUserLoggedIn username
    if status then
        printf $"Request received to search hashtag: {hashtag}"
        let hashTagList = hashTagsMap.TryFind(hashtag)
        if hashTagList = None then
            getResponseMessage($"No Tweets found with hashtag: {hashtag}", [], 2, false)
        else
            let response = [for item in 1 .. (Math.Min(5, hashTagList.Value.Count)) do yield(hashTagList.Value.[item-1])] 
            getResponseMessage($"Hashtags get done", response, 2, false)
    else
        resp

let getMentionsList username = 
    let (resp,status) = isUserLoggedIn username
    if status then
        let mentionList = mentionsMap.TryFind(username)
        if mentionList = None then
            getResponseMessage($"No mentions found", [], 2, false)
        else
            let mentionSubList = new List<string>()
            for mention in mentionList.Value do
                for tweet in mention.Value do
                    mentionSubList.Add(tweet)
            let result = [for item in 1 .. (Math.Min(5, mentionSubList.Count)) do yield(mentionSubList.[item-1])]
            getResponseMessage($"Mentions get done", result, 2, false)
    else
        resp

let setHeaders rspMsg =
    rspMsg
    |> JsonConvert.SerializeObject
    |> OK
    >=> setMimeType "application/json"
    >=> setCORSHeaders

let getUserTweets username =
    printfn "GetTweets request received: %s " username
    getTweetsList username
    |> setHeaders 

let getUserMentions username =
    printfn "GetMentions request received: %s " username
    getMentionsList username
    |> setHeaders 
 
let getUserHashtags username hashtag =
    printfn "GetHashTag request received: %s hashtag: %A" username hashtag
    getHashTagsList username hashtag
    |> setHeaders

let postResponse (operation) =
    let subresponse input = 
        match operation with 
        | "Register" -> 
                input 
                |> getJsonObject<Register>
                |> UserRegister
        | "Login" -> 
                input 
                |> getJsonObject<Login>
                |> UserLogin
        | "Logout" -> 
                input 
                |> getJsonObject<Logout>
                |> UserLogout
        | "NewTweet" -> 
                input 
                |> getJsonObject<NewTweet>
                |> addTweetToUser
        | "Follow" -> 
                input 
                |> getJsonObject<Follower>
                |> UpdateFollowers 
        | _ -> getResponseMessage("Operation not found", [], 0, true)

    request (fun context ->
    context.rawForm
    |> getString
    |> subresponse
    |> JsonConvert.SerializeObject
    |> OK
    )
    >=> setMimeType "application/json"
    >=> setCORSHeaders


//setup app routes
let app =
    choose
        [ 
            path "/websocket" >=> handShake websocketHandler 

            choose [
                OPTIONS >=>
                    fun context ->
                        context |> (
                            setCORSHeaders
                            >=> OK "CORS accepted" )
            ]

            GET >=> choose
                [ 
                path "/" >=> OK "Server started..." 
                path "/home" >=> file "public/index.html";
                path "/register" >=> file "public/register.html"; 
                path "/user" >=> file "public/user.html";
                path "/logo" >=> file "resources/tweet.png";
                pathScan "/search/gettweets/%s" (fun username -> (getUserTweets username))
                pathScan "/search/getmentions/%s" (fun username -> (getUserMentions username))
                pathScan "/search/gethashtags/%s/%s" (fun (username,hashtag) -> (getUserHashtags username hashtag))
                ]

            POST >=> choose
                [   
                path "/user/register" >=> (fun context -> context |> postResponse("Register"))
                path "/user/login" >=> (fun context -> context |> postResponse("Login"))
                path "/user/logout" >=> (fun context -> context |> postResponse("Logout"))
                path "/user/newtweet" >=> (fun context -> context |> postResponse("NewTweet")) 
                path "/user/follow" >=> (fun context -> context |> postResponse("Follow"))
              ]

            NOT_FOUND "404 - Page not found!"
        ]

[<EntryPoint>]
let main argv =
    startWebServer defaultConfig app
    0
