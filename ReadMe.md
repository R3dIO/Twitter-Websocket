# COP5615 - Distributed Operating System Principles
## Project 4 Part 2
## Twitter Clone with Websocket implementation

Author: 
Anuj Koli - UFID: 97977572 
Pratiksha Jain - UFID: 96115195

## Introduction
In this project we have implemented a twitter clone. We have reused the twitter engine implemented for part 1 of this project and exposed REST API for each function. We added websocket interface to consume those APIs and added basic UI on top of it. 

## What is working
### Rest APIs Implementation


### Websocket Implementation


### Request and Response formats
| Request Type | Request Endpoint | Request parameters | Response body | Response comments |
|-----------|--------------|----------------|----------------|----------------|
| POST | /register | { Username: < username >, Password: < password > } | | Success/Failure |
| POST | /login | { Username: < username >, Password: < password > } | | Success/Incorrect password/New user |
| POST | /logout | { Username: < username > } | | Success/Not logged in/New user |
| POST | /newtweet | { Username: < username >, Tweet: < tweet text > } | | Success/Not logged in/New user |
| POST | /follow | { Username: < username >, Following: < follow user > } | | Success/Not logged in/New user/Follower does not exist/Already Following |
| GET | /gettweets/username | | { tweet1, tweet2... } | Success/Not logged in/New user |
| GET | /gethashtags/username/hashtag | | hashtags in { tweet1, tweet2... } | Success/Not logged in/New user |
| GET | /getmentions/username | | mentions in { tweet1, tweet2... } | Success/Not logged in/New user |