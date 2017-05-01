//-----------------------------------------------------------------------
// <copyright file="AddressUidExtension.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using Akka.Actor;
using Akka.Util;

namespace Akka.Remote
{
    /// <summary>
    /// Extension that holds a UID that is assigned as a random 'Int'.
    /// 
    /// The UID is intended to be used together with an <see cref="Address"/> to be
    /// able to distinguish restarted actor system using the same host and port.
    /// </summary>
    public class AddressUid : IExtension
    {
        #region Static methods

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="system">TBD</param>
        /// <returns>TBD</returns>
        public static int GetUid(ActorSystem system)
        {
            return system.WithExtension<AddressUid>().Uid;
        }

        #endregion

        /// <summary>
        /// TBD
        /// </summary>
        public readonly int Uid = ThreadLocalRandom.Current.Next();
    }
}

