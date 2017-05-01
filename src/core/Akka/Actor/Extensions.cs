//-----------------------------------------------------------------------
// <copyright file="Extensions.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;

namespace Akka.Actor
{
    /// <summary>
    /// This interface is used to mark an object as an <see cref="ActorSystem"/> extension.
    /// </summary>
    public interface IExtension { }

    /// <summary>
    /// This class contains extension methods used for resolving <see cref="ActorSystem"/> extensions.
    /// </summary>
    public static class ActorSystemWithExtensions
    {
        /// <summary>
        /// Retrieves the extension specified by a given type, <typeparamref name="T"/>, from a given actor system.
        /// If the extension does not exist within the actor system, then the extension specified by <typeparamref name="T"/>
        /// is registered to the actor system.
        /// </summary>
        /// <typeparam name="T">The type associated with the extension to retrieve.</typeparam>
        /// <param name="system">The actor system from which to retrieve the extension or to register with if it does not exist.</param>
        /// <returns>The extension retrieved from the given actor system.</returns>
        public static T WithExtension<T>(this ActorSystem system) where T : class, IExtension
        {
            if (system.HasExtension<T>())
                return system.GetExtension<T>();
            else
            {
                return (T) system.DependencyResolver.Resolve(typeof(T));
            }
        }

        /// <summary>
        /// Retrieves the extension specified by a given type, <typeparamref name="TInterface"/>, from a given actor system.
        /// If the extension does not exist within the actor system, then the extension specified by <paramref name="TImpl"/>
        /// is registered to the actor system.
        /// </summary>
        /// <typeparam name="TInterface">The type associated with the extension to retrieve.</typeparam>
        /// <typeparam name="TImpl">An actual impl of <typeparamref name="TInterface"/> to be used.</typeparam>
        /// <param name="system">The actor system from which to retrieve the extension or to register with if it does not exist.</param>
        /// <returns>The extension retrieved from the given actor system.</returns>
        public static TInterface WithExtension<TInterface, TImpl>(this ActorSystem system) 
            where TInterface : class, IExtension
            where TImpl : TInterface
        {
            if (system.HasExtension<TInterface>())
                return system.GetExtension<TInterface>();
            else
            {
                system.DependencyResolver.Register(typeof(TInterface), typeof(TImpl));
                return (TInterface)system.DependencyResolver.Resolve(typeof(TInterface));
            }
        }
    }
}
