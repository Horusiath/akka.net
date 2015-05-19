//-----------------------------------------------------------------------
// <copyright file="Spawning.fs" company="Akka.NET Project">
//     Copyright (C) 2009-2015 Typesafe Inc. <http://www.typesafe.com>
//     Copyright (C) 2013-2015 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------
namespace Akka.FSharp

open Akka.Actor
open System
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Linq.QuotationEvaluation

[<RequireQualifiedAccess>]
module Configuration =
    /// Parses provided HOCON string into a valid Akka configuration object.
    let parse = Akka.Configuration.ConfigurationFactory.ParseString

    /// Returns default Akka configuration.
    let defaultConfig = Akka.Configuration.ConfigurationFactory.Default

    /// Loads Akka configuration from the project's .config file.
    let load = Akka.Configuration.ConfigurationFactory.Load

module System =
    /// Creates an actor system with remote deployment serialization enabled.
    let create (name : string) (config : Akka.Configuration.Config) : ActorSystem =
        let system = ActorSystem.Create(name, config)
        Serialization.exprSerializationSupport system
        system

[<AutoOpen>]
module Spawn =
    type SpawnOption =
        | Deploy of Deploy
        | Router of Akka.Routing.RouterConfig
        | SupervisorStrategy of SupervisorStrategy
        | Dispatcher of string
        | Mailbox of string

    let rec applySpawnOptions opt (props : Props) =
        match opt with
        | [] -> props
        | h :: t ->
            let p =
                match h with
                | Deploy d -> props.WithDeploy d
                | Router r -> props.WithRouter r
                | SupervisorStrategy s -> props.WithSupervisorStrategy s
                | Dispatcher d -> props.WithDispatcher d
                | Mailbox m -> props.WithMailbox m
            applySpawnOptions t p

    let spawnFromProps (actorFactory : IActorRefFactory) name props =
        actorFactory.ActorOf(props, name)
        |> typed
        :> ActorRef<'Message>

    let createFromExpression actorFactory options name : Linq.Expressions.Expression<Func<'TActor>> -> ActorRef<'Message>=
        Props.Create
        >> applySpawnOptions options
        >> spawnFromProps actorFactory name

    /// <summary>
    /// Spawns an actor using specified actor computation expression, using an Expression AST.
    /// The actor code can be deployed remotely.
    /// </summary>
    /// <param name="actorFactory">Either actor system or parent actor</param>
    /// <param name="name">Name of spawned child actor</param>
    /// <param name="actorExpr">F# expression compiled down to receive function used by actor for response for incoming request</param>
    /// <param name="options">List of options used to configure actor creation</param>
    let spawne actorFactory options name (actorExpr : Expr<Actor<'Message> -> Cont<'Message, 'Returned>>) : ActorRef<'Message> =
        (fun () -> FunActor<_,_>(actorExpr))
        |> Linq.Expression.ToExpression
        |> createFromExpression actorFactory options name

    /// <summary>
    /// Spawns an actor using specified actor computation expression, with custom spawn option settings.
    /// The actor can only be used locally.
    /// </summary>
    /// <param name="actorFactory">Either actor system or parent actor</param>
    /// <param name="name">Name of spawned child actor</param>
    /// <param name="actor">Used by actor for handling response for incoming request</param>
    /// <param name="options">List of options used to configure actor creation</param>
    let spawnOpt actorFactory options name (actor : Actor<'Message> -> Cont<'Message, 'Returned>) : ActorRef<'Message> =
        (fun () -> FunActor<_,_>(actor))
        |> Linq.Expression.ToExpression
        |> createFromExpression actorFactory options name

    /// <summary>
    /// Spawns an actor using specified actor computation expression.
    /// The actor can only be used locally.
    /// </summary>
    /// <param name="actorFactory">Either actor system or parent actor</param>
    /// <param name="name">Name of spawned child actor</param>
    /// <param name="actor">Used by actor for handling response for incoming request</param>
    let spawn actorFactory name (actor : Actor<'Message> -> Cont<'Message, 'Returned>) : ActorRef<'Message> =
        spawnOpt actorFactory [] name actor

    /// <summary>
    /// Spawns an actor using specified actor quotation, with custom spawn option settings.
    /// The actor can only be used locally.
    /// </summary>
    /// <param name="actorFactory">Either actor system or parent actor</param>
    /// <param name="name">Name of spawned child actor</param>
    /// <param name="actorExpr">Used to create a new instance of the actor</param>
    /// <param name="options">List of options used to configure actor creation</param>
    let spawnObjOpt actorFactory options name (actorExpr : Quotations.Expr<unit -> #ActorBase>) : ActorRef<'Message> =
        Linq.Expression.ToExpression<'Actor> actorExpr
        |> createFromExpression actorFactory options name

    /// <summary>
    /// Spawns an actor using specified actor quotation.
    /// The actor can only be used locally.
    /// </summary>
    /// <param name="actorFactory">Either actor system or parent actor</param>
    /// <param name="name">Name of spawned child actor</param>
    /// <param name="actorExpr">Used to create a new instance of the actor</param>
    let spawnObj actorFactory name (actorExpr : Quotations.Expr<unit -> #ActorBase>) : ActorRef<'Message> =
        spawnObjOpt actorFactory [] name actorExpr

    /// <summary>
    /// Wraps provided function with actor behavior.
    /// It will be invoked each time, an actor will receive a message.
    /// </summary>
    let actorOf (fn : 'Message -> unit) : Actor<'Message> -> Cont<'Message, 'Returned> =
        fun mbx ->
            let rec loop() = actor {
                let! msg = mbx.Receive()
                fn msg
                return! loop()
            }
            loop()

    /// <summary>
    /// Wraps provided function with actor behavior.
    /// It will be invoked each time, an actor will receive a message.
    /// </summary>
    let actorOf2 (fn : Actor<'Message> -> 'Message -> unit) : Actor<'Message> -> Cont<'Message, 'Returned> =
        fun mbx ->
            let rec loop() = actor {
                let! msg = mbx.Receive()
                fn mbx msg
                return! loop()
            }
            loop()
