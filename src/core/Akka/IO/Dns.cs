﻿//-----------------------------------------------------------------------
// <copyright file="Dns.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Akka.Actor;
using Akka.Configuration;
using Akka.Routing;

namespace Akka.IO
{
    /// <summary>
    /// TBD
    /// </summary>
    public abstract class DnsBase
    {
        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="name">TBD</param>
        /// <returns>TBD</returns>
        public virtual Dns.Resolved Cached(string name)
        {
            return null;
        }
        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="name">TBD</param>
        /// <param name="system">TBD</param>
        /// <param name="sender">TBD</param>
        /// <returns>TBD</returns>
        public virtual Dns.Resolved Resolve(string name, ActorSystem system, IActorRef sender)
        {
            var ret = Cached(name);
            if (ret == null)
                Dns.Get(system).Manager.Tell(new Dns.Resolve(name), sender);
            return ret;
        }
    }

    /// <summary>
    /// TBD
    /// </summary>
    public class Dns : IOExtension
    {
        /// <summary>
        /// TBD
        /// </summary>
        public abstract class Command
        { }

        /// <summary>
        /// TBD
        /// </summary>
        public class Resolve : Command, IConsistentHashable
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="name">TBD</param>
            public Resolve(string name)
            {
                Name = name;
                ConsistentHashKey = name;
            }

            /// <summary>
            /// TBD
            /// </summary>
            public object ConsistentHashKey { get; private set; }
            /// <summary>
            /// TBD
            /// </summary>
            public string Name { get; private set; }
        }

        /// <summary>
        /// TBD
        /// </summary>
        public class Resolved : Command
        {
            private readonly IPAddress _addr;

            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="name">TBD</param>
            /// <param name="ipv4">TBD</param>
            /// <param name="ipv6">TBD</param>
            public Resolved(string name, IEnumerable<IPAddress> ipv4, IEnumerable<IPAddress> ipv6)
            {
                Name = name;
                Ipv4 = ipv4;
                Ipv6 = ipv6;

                _addr = ipv4.FirstOrDefault() ?? ipv6.FirstOrDefault();
            }

            /// <summary>
            /// TBD
            /// </summary>
            public string Name { get; private set; }
            /// <summary>
            /// TBD
            /// </summary>
            public IEnumerable<IPAddress> Ipv4 { get; private set; }
            /// <summary>
            /// TBD
            /// </summary>
            public IEnumerable<IPAddress> Ipv6 { get; private set; }

            /// <summary>
            /// TBD
            /// </summary>
            public IPAddress Addr
            {
                get
                {
                    //TODO: Throw better exception
                    if (_addr == null) throw new Exception("Unknown host");
                    return _addr;
                }
            }

            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="name">TBD</param>
            /// <param name="addresses">TBD</param>
            /// <returns>TBD</returns>
            public static Resolved Create(string name, IEnumerable<IPAddress> addresses)
            {
                var ipv4 = addresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork);
                var ipv6 = addresses.Where(x => x.AddressFamily == AddressFamily.InterNetworkV6);
                return new Resolved(name, ipv4, ipv6);
            }
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="name">TBD</param>
        /// <param name="system">TBD</param>
        /// <returns>TBD</returns>
        public static Resolved Cached(string name, ActorSystem system)
        {
            return Dns.Get(system).Cache.Cached(name);
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="name">TBD</param>
        /// <param name="system">TBD</param>
        /// <param name="sender">TBD</param>
        /// <returns>TBD</returns>
        public static Resolved ResolveName(string name, ActorSystem system, IActorRef sender)
        {
            return Dns.Get(system).Cache.Resolve(name, system, sender);
        }

        /// <summary>
        /// TBD
        /// </summary>
        public class DnsSettings
        {
            /// <summary>
            /// TBD
            /// </summary>
            /// <param name="config">TBD</param>
            public DnsSettings(Config config)
            {
                Dispatcher = config.GetString("dispatcher");
                Resolver = config.GetString("resolver");
                ResolverConfig = config.GetConfig(Resolver);
                ProviderObjectName = ResolverConfig.GetString("provider-object");
            }

            /// <summary>
            /// TBD
            /// </summary>
            public string Dispatcher { get; private set; }
            /// <summary>
            /// TBD
            /// </summary>
            public string Resolver { get; private set; }
            /// <summary>
            /// TBD
            /// </summary>
            public Config ResolverConfig { get; private set; }
            /// <summary>
            /// TBD
            /// </summary>
            public string ProviderObjectName { get; private set; }
        }
        
        private readonly ExtendedActorSystem _system;
        private IActorRef _manager;

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="system">TBD</param>
        public Dns(ExtendedActorSystem system)
        {
            _system = system;
            Settings = new DnsSettings(system.Settings.Config.GetConfig("akka.io.dns"));
            //TODO: system.dynamicAccess.getClassFor[DnsProvider](Settings.ProviderObjectName).get.newInstance()
            var dnsProviderType = Type.GetType(Settings.ProviderObjectName, true);
            if (!typeof(IDnsProvider).IsAssignableFrom(dnsProviderType))
                throw new ArgumentException($"{Settings.ProviderObjectName} doesn't implement {nameof(IDnsProvider)} interface.");

            Provider = (IDnsProvider) system.DependencyResolver.Resolve(dnsProviderType);
            Cache = Provider.Cache;
        }

        /// <summary>
        /// TBD
        /// </summary>
        public override IActorRef Manager
        {
            get
            {
                return _manager = _manager ?? _system.SystemActorOf(Props.Create(() => new SimpleDnsManager(this))
                                                                         .WithDeploy(Deploy.Local)
                                                                         .WithDispatcher(Settings.Dispatcher));
            }
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <returns>TBD</returns>
        public IActorRef GetResolver()
        {
            return _manager;
        }

        /// <summary>
        /// TBD
        /// </summary>
        public DnsSettings Settings { get; private set; }
        /// <summary>
        /// TBD
        /// </summary>
        public DnsBase Cache { get; private set; }
        /// <summary>
        /// TBD
        /// </summary>
        public IDnsProvider Provider { get; private set; }

        public static Dns Get(ActorSystem system)
        {
            return system.WithExtension<Dns>();
        }
    }
}
