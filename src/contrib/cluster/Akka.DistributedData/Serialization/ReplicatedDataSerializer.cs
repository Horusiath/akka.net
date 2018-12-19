﻿//-----------------------------------------------------------------------
// <copyright file="ReplicatedDataSerializer.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2018 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2018 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using Akka.Actor;
using Akka.Configuration;
using Akka.DistributedData.Internal;
using Akka.Serialization;
using Google.Protobuf;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Akka.DistributedData.Serialization
{
    public sealed class ReplicatedDataSerializer : SerializerWithStringManifest, IWithSerializationSupport
    {
        private const string DeletedDataManifest = "A";
        private const string GSetManifest = "B";
        private const string GSetKeyManifest = "b";
        private const string ORSetManifest = "C";
        private const string ORSetKeyManifest = "c";
        private const string ORSetAddManifest = "Ca";
        private const string ORSetRemoveManifest = "Cr";
        private const string ORSetFullManifest = "Cf";
        private const string ORSetDeltaGroupManifest = "Cg";
        private const string FlagManifest = "D";
        private const string FlagKeyManifest = "d";
        private const string LWWRegisterManifest = "E";
        private const string LWWRegisterKeyManifest = "e";
        private const string GCounterManifest = "F";
        private const string GCounterKeyManifest = "f";
        private const string PNCounterManifest = "G";
        private const string PNCounterKeyManifest = "g";
        private const string ORMapManifest = "H";
        private const string ORMapKeyManifest = "h";
        private const string ORMapPutManifest = "Ha";
        private const string ORMapRemoveManifest = "Hr";
        private const string ORMapRemoveKeyManifest = "Hk";
        private const string ORMapUpdateManifest = "Hu";
        private const string ORMapDeltaGroupManifest = "Hg";
        private const string LWWMapManifest = "I";
        private const string LWWMapKeyManifest = "i";
        private const string PNCounterMapManifest = "J";
        private const string PNCounterMapKeyManifest = "j";
        private const string ORMultiMapManifest = "K";
        private const string ORMultiMapKeyManifest = "k";
        private const string VersionVectorManifest = "L";
        
        private readonly TypeMap _mappings;
        private string _protocol;
        public string Protocol
        {
            get
            {
                var p = Volatile.Read(ref _protocol);
                if (ReferenceEquals(p, null))
                {
                    p = system.Provider.DefaultAddress.Protocol;
                    Volatile.Write(ref _protocol, p);
                }

                return p;
            }
        }

        public Actor.ActorSystem System
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => system;
        }
        public Akka.Serialization.Serialization Serialization
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => system.Serialization;
        }

        private readonly byte[] _emptyArray = new byte[0];

        public ReplicatedDataSerializer(ExtendedActorSystem system, Config config) : base(system)
        {
            _mappings = new TypeMap(config.GetConfig("mappings"));
        }

        public override string Manifest(object o)
        {
            switch (o)
            {
                case DeletedData _: return DeletedDataManifest;
                case VersionVector _: return VersionVectorManifest;
                case GCounter _: return GCounterManifest;
                case PNCounter _: return PNCounterManifest;
                case Flag _: return FlagManifest;
                case IReplicatedData _:
                    {
                        dynamic d = o;
                        return Manifest(d);
                    }
                case GCounterKey _: return GCounterKeyManifest;
                case PNCounterKey _: return PNCounterKeyManifest;
                case FlagKey _: return FlagKeyManifest;
                case IKey _:
                    {
                        dynamic d = o;
                        return Manifest(d);
                    }
                default: throw new ArgumentException($"Can't serialize object of type [{o.GetType().FullName}] in [{GetType().FullName}]");
            }
        }

        private static string Manifest<T>(ORSet<T> _) => ORSetManifest;
        private static string Manifest<T>(ORSet<T>.AddDeltaOperation _) => ORSetAddManifest;
        private static string Manifest<T>(ORSet<T>.RemoveDeltaOperation _) => ORSetRemoveManifest;
        private static string Manifest<T>(GSet<T> _) => GSetManifest;
        private static string Manifest<T>(LWWRegister<T> _) => LWWRegisterManifest;
        private static string Manifest<T>(ORSet<T>.DeltaGroup _) => ORSetDeltaGroupManifest;
        private static string Manifest<T>(ORSet<T>.FullStateDeltaOperation _) => ORSetFullManifest;
        private static string Manifest<TKey, TVal>(ORDictionary<TKey, TVal> _) where TVal: IReplicatedData<TVal> => ORMapManifest;
        private static string Manifest<TKey, TVal>(ORDictionary<TKey, TVal>.PutDeltaOperation _) where TVal : IReplicatedData<TVal> => ORMapPutManifest;
        private static string Manifest<TKey, TVal>(ORDictionary<TKey, TVal>.RemoveDeltaOperation _) where TVal : IReplicatedData<TVal> => ORMapRemoveManifest;
        private static string Manifest<TKey, TVal>(ORDictionary<TKey, TVal>.RemoveKeyDeltaOperation _) where TVal : IReplicatedData<TVal> => ORMapRemoveKeyManifest;
        private static string Manifest<TKey, TVal>(ORDictionary<TKey, TVal>.UpdateDeltaOperation _) where TVal : IReplicatedData<TVal> => ORMapUpdateManifest;
        private static string Manifest<TKey, TVal>(LWWDictionary<TKey, TVal> _) => LWWMapManifest;
        private static string Manifest<TKey>(PNCounterDictionary<TKey> _) => PNCounterMapManifest;
        private static string Manifest<TKey, TVal>(ORMultiValueDictionary<TKey, TVal> _) => ORMultiMapManifest;
        
        private static string Manifest<T>(ORSetKey<T> _) => ORSetKeyManifest;
        private static string Manifest<T>(GSetKey<T> _) => GSetKeyManifest;
        private static string Manifest<T>(PNCounterDictionaryKey<T> _) => PNCounterMapKeyManifest;
        private static string Manifest<TKey, TVal>(ORDictionaryKey<TKey, TVal> _) where TVal : IReplicatedData<TVal> => ORMapKeyManifest;
        private static string Manifest<TKey, TVal>(LWWDictionaryKey<TKey, TVal> _) => LWWMapKeyManifest;
        private static string Manifest<TKey, TVal>(ORMultiValueDictionaryKey<TKey, TVal> _) => ORMultiMapKeyManifest;


        public override byte[] ToBinary(object o)
        {
            switch (o)
            {
                case DeletedData _: return _emptyArray;
                case VersionVector _: return ((VersionVector)o).ToProto().ToByteArray();
                case GCounter _: return ToProto((GCounter)o).ToByteArray();
                case PNCounter _: return ToProto((PNCounter)o).ToByteArray();
                case Flag _: return ToProto((Flag)o).ToByteArray();
                case IKey _:
                {
                    dynamic k = o;
                    return ToBinary(k);
                }
                case IReplicatedData _:
                    {
                        dynamic d = o;
                        return ToBinary(d);
                    }
                default: throw new ArgumentException($"Can't serialize object of type [{o.GetType().FullName}] in [{GetType().FullName}]");
            }
        }

        #region serialize keys

        private byte[] ToBinary(FlagKey key) => new Proto.Msg.Key { Path = ByteString.CopyFromUtf8(key.Id) }.ToByteArray();
        private byte[] ToBinary(GCounterKey key) => new Proto.Msg.Key { Path = ByteString.CopyFromUtf8(key.Id) }.ToByteArray();
        private byte[] ToBinary(PNCounterKey key) => new Proto.Msg.Key { Path = ByteString.CopyFromUtf8(key.Id) }.ToByteArray();
        private byte[] ToBinary<T>(GSetKey<T> key) => 
            new Proto.Msg.Key { Path = ByteString.CopyFromUtf8(key.Id), ValueCode = _mappings[typeof(T)]}.ToByteArray();
        private byte[] ToBinary<T>(ORSetKey<T> key) =>
            new Proto.Msg.Key { Path = ByteString.CopyFromUtf8(key.Id), ValueCode = _mappings[typeof(T)] }.ToByteArray();
        private byte[] ToBinary<T>(LWWRegisterKey<T> key) =>
            new Proto.Msg.Key { Path = ByteString.CopyFromUtf8(key.Id), ValueCode = _mappings[typeof(T)] }.ToByteArray();
        private byte[] ToBinary<T>(PNCounterDictionaryKey<T> key) =>
            new Proto.Msg.Key { Path = ByteString.CopyFromUtf8(key.Id), ValueCode = _mappings[typeof(T)] }.ToByteArray();
        private byte[] ToBinary<TKey, TValue>(ORDictionaryKey<TKey, TValue> key) where TValue : IReplicatedData<TValue> =>
            new Proto.Msg.Key { Path = ByteString.CopyFromUtf8(key.Id), KeyCode = _mappings[typeof(TKey)], ValueCode = _mappings[typeof(TValue)] }.ToByteArray();
        private byte[] ToBinary<TKey, TValue>(LWWDictionaryKey<TKey, TValue> key) =>
            new Proto.Msg.Key { Path = ByteString.CopyFromUtf8(key.Id), KeyCode = _mappings[typeof(TKey)], ValueCode = _mappings[typeof(TValue)] }.ToByteArray();
        private byte[] ToBinary<TKey, TValue>(ORMultiValueDictionaryKey<TKey, TValue> key) =>
            new Proto.Msg.Key { Path = ByteString.CopyFromUtf8(key.Id), KeyCode = _mappings[typeof(TKey)], ValueCode = _mappings[typeof(TValue)] }.ToByteArray();

        #endregion

        #region serialize GSet

        private byte[] ToBinary(GSet<long> o) => ToProto(o).ToByteArray();
        private Proto.Msg.GSet ToProto(GSet<long> o)
        {
            var proto = new Proto.Msg.GSet
            {
                TypeCode = _mappings[typeof(long)]
            };
            foreach (var e in o.Elements)
                proto.LongElements.Add(e);
            return proto;
        }

        private byte[] ToBinary(GSet<int> o) => ToProto(o).ToByteArray();
        private Proto.Msg.GSet ToProto(GSet<int> o)
        {
            var proto = new Proto.Msg.GSet
            {
                TypeCode = _mappings[typeof(int)]
            };
            foreach (var e in o.Elements)
                proto.IntElements.Add(e);
            return proto;
        }

        private byte[] ToBinary(GSet<string> o) => ToProto(o).ToByteArray();
        private Proto.Msg.GSet ToProto(GSet<string> o)
        {
            var proto = new Proto.Msg.GSet
            {
                TypeCode = _mappings[typeof(string)]
            };
            foreach (var e in o.Elements)
                proto.StringElements.Add(e);
            return proto;
        }

        private byte[] ToBinary(GSet<IActorRef> o) => ToProto(o).ToByteArray();
        private Proto.Msg.GSet ToProto(GSet<IActorRef> o)
        {
            var proto = new Proto.Msg.GSet
            {
                TypeCode = _mappings[typeof(IActorRef)]
            };
            foreach (var e in o.Elements)
                proto.ActorRefElements.Add(Akka.Serialization.Serialization.SerializedActorPath(e));
            return proto;
        }

        private byte[] ToBinary<T>(GSet<T> o) => ToProto<T>(o).ToByteArray();
        private Proto.Msg.GSet ToProto<T>(GSet<T> o)
        {
            var proto = new Proto.Msg.GSet
            {
                TypeCode = _mappings[typeof(T)]
            };
            foreach (object e in o.Elements)
                proto.OtherElements.Add(this.OtherMessageToProto(e));
            return proto;
        }

        #endregion

        #region serialize ORSet

        private byte[] ToBinary(ORSet<int> o) => ToProto(o).Compress();
        private Proto.Msg.ORSet ToProto(ORSet<int> o)
        {
            var proto = new Proto.Msg.ORSet
            {
                TypeCode = _mappings[typeof(int)],
                Vvector = o.VersionVector.ToProto()
            };

            foreach (var e in o.ElementsMap)
            {
                proto.IntElements.Add(e.Key);
                proto.Dots.Add(e.Value.ToProto());
            }
            return proto;
        }

        private byte[] ToBinary(ORSet<long> o) => ToProto(o).Compress();
        private Proto.Msg.ORSet ToProto(ORSet<long> o)
        {
            var proto = new Proto.Msg.ORSet
            {
                TypeCode = _mappings[typeof(long)],
                Vvector = o.VersionVector.ToProto()
            };

            foreach (var e in o.ElementsMap)
            {
                proto.LongElements.Add(e.Key);
                proto.Dots.Add(e.Value.ToProto());
            }
            return proto;
        }

        private byte[] ToBinary(ORSet<string> o) => ToProto(o).Compress();
        private Proto.Msg.ORSet ToProto(ORSet<string> o)
        {
            var proto = new Proto.Msg.ORSet
            {
                TypeCode = _mappings[typeof(string)],
                Vvector = o.VersionVector.ToProto()
            };

            foreach (var e in o.ElementsMap)
            {
                proto.StringElements.Add(e.Key);
                proto.Dots.Add(e.Value.ToProto());
            }

            return proto;
        }

        private byte[] ToBinary(ORSet<IActorRef> o) => ToProto(o).Compress();
        private Proto.Msg.ORSet ToProto(ORSet<IActorRef> o)
        {
            var proto = new Proto.Msg.ORSet
            {
                TypeCode = _mappings[typeof(IActorRef)],
                Vvector = o.VersionVector.ToProto()
            };

            foreach (var e in o.ElementsMap)
            {
                proto.ActorRefElements.Add(Akka.Serialization.Serialization.SerializedActorPath(e.Key));
                proto.Dots.Add(e.Value.ToProto());
            }
            return proto;
        }

        private byte[] ToBinary<T>(ORSet<T> o) => ToProto<T>(o).Compress();
        private Proto.Msg.ORSet ToProto<T>(ORSet<T> o)
        {
            var proto = new Proto.Msg.ORSet
            {
                TypeCode = _mappings[typeof(T)],
                Vvector = o.VersionVector.ToProto()
            };

            foreach (var e in o.ElementsMap)
            {
                proto.OtherElements.Add(this.OtherMessageToProto(e.Key));
                proto.Dots.Add(e.Value.ToProto());
            }

            return proto;
        }

        #endregion

        #region serialize ORSet.AddDeltaOp

        private byte[] ToBinary(ORSet<int>.AddDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<int>.AddDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary(ORSet<long>.AddDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<long>.AddDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary(ORSet<string>.AddDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<string>.AddDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary(ORSet<IActorRef>.AddDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<IActorRef>.AddDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary<T>(ORSet<T>.AddDeltaOperation o) => ToProto<T>(o).ToByteArray();
        private Proto.Msg.ORSet ToProto<T>(ORSet<T>.AddDeltaOperation o) => ToProto<T>(o.Underlying);
        #endregion

        #region serialize ORSet.RemoveDeltaOp

        private byte[] ToBinary(ORSet<int>.RemoveDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<int>.RemoveDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary(ORSet<long>.RemoveDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<long>.RemoveDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary(ORSet<string>.RemoveDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<string>.RemoveDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary(ORSet<IActorRef>.RemoveDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<IActorRef>.RemoveDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary<T>(ORSet<T>.RemoveDeltaOperation o) => ToProto<T>(o).ToByteArray();
        private Proto.Msg.ORSet ToProto<T>(ORSet<T>.RemoveDeltaOperation o) => ToProto<T>(o.Underlying);
        #endregion

        #region serialize ORSet.GroupDeltaOp

        private byte[] ToBinary(ORSet<int>.DeltaGroup o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSetDeltaGroup ToProto(ORSet<int>.DeltaGroup o)
        {
            var proto = new Proto.Msg.ORSetDeltaGroup();
            foreach (var delta in o.Operations)
            {
                switch (delta)
                {
                    case ORSet<int>.AddDeltaOperation add:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Add,
                            Underlying = ToProto(add.Underlying)
                        });
                        break;
                    case ORSet<int>.RemoveDeltaOperation rem:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Remove,
                            Underlying = ToProto(rem.Underlying)
                        });
                        break;
                    case ORSet<int>.FullStateDeltaOperation full:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Full,
                            Underlying = ToProto(full.Underlying)
                        });
                        break;
                    default: throw new ArgumentException($"{delta} should not be nested");
                }

            }
            return proto;
        }

        private byte[] ToBinary(ORSet<long>.DeltaGroup o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSetDeltaGroup ToProto(ORSet<long>.DeltaGroup o)
        {
            var proto = new Proto.Msg.ORSetDeltaGroup();
            foreach (var delta in o.Operations)
            {
                switch (delta)
                {
                    case ORSet<long>.AddDeltaOperation add:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Add,
                            Underlying = ToProto(add.Underlying)
                        });
                        break;
                    case ORSet<long>.RemoveDeltaOperation rem:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Remove,
                            Underlying = ToProto(rem.Underlying)
                        });
                        break;
                    case ORSet<long>.FullStateDeltaOperation full:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Full,
                            Underlying = ToProto(full.Underlying)
                        });
                        break;
                    default: throw new ArgumentException($"{delta} should not be nested");
                }

            }
            return proto;
        }

        private byte[] ToBinary(ORSet<string>.DeltaGroup o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSetDeltaGroup ToProto(ORSet<string>.DeltaGroup o)
        {
            var proto = new Proto.Msg.ORSetDeltaGroup();
            foreach (var delta in o.Operations)
            {
                switch (delta)
                {
                    case ORSet<string>.AddDeltaOperation add:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Add,
                            Underlying = ToProto(add.Underlying)
                        });
                        break;
                    case ORSet<string>.RemoveDeltaOperation rem:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Remove,
                            Underlying = ToProto(rem.Underlying)
                        });
                        break;
                    case ORSet<string>.FullStateDeltaOperation full:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Full,
                            Underlying = ToProto(full.Underlying)
                        });
                        break;
                    default: throw new ArgumentException($"{delta} should not be nested");
                }

            }
            return proto;
        }

        private byte[] ToBinary(ORSet<IActorRef>.DeltaGroup o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSetDeltaGroup ToProto(ORSet<IActorRef>.DeltaGroup o)
        {
            var proto = new Proto.Msg.ORSetDeltaGroup();
            foreach (var delta in o.Operations)
            {
                switch (delta)
                {
                    case ORSet<IActorRef>.AddDeltaOperation add:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Add,
                            Underlying = ToProto(add.Underlying)
                        });
                        break;
                    case ORSet<IActorRef>.RemoveDeltaOperation rem:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Remove,
                            Underlying = ToProto(rem.Underlying)
                        });
                        break;
                    case ORSet<IActorRef>.FullStateDeltaOperation full:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Full,
                            Underlying = ToProto(full.Underlying)
                        });
                        break;
                    default: throw new ArgumentException($"{delta} should not be nested");
                }

            }
            return proto;
        }

        private byte[] ToBinary<T>(ORSet<T>.DeltaGroup o) => ToProto<T>(o).ToByteArray();
        private IMessage ToProto<T>(ORSet<T>.DeltaGroup o)
        {
            var proto = new Proto.Msg.ORSetDeltaGroup();
            foreach (var delta in o.Operations)
            {
                switch (delta)
                {
                    case ORSet<T>.AddDeltaOperation add:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Add,
                            Underlying = (Proto.Msg.ORSet)ToProto<T>(add.Underlying)
                        });
                        break;
                    case ORSet<T>.RemoveDeltaOperation rem:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Remove,
                            Underlying = (Proto.Msg.ORSet)ToProto<T>(rem.Underlying)
                        });
                        break;
                    case ORSet<T>.FullStateDeltaOperation full:
                        proto.Entries.Add(new Proto.Msg.ORSetDeltaGroup.Types.Entry
                        {
                            Operation = Proto.Msg.ORSetDeltaOp.Full,
                            Underlying = (Proto.Msg.ORSet)ToProto<T>(full.Underlying)
                        });
                        break;
                    default: throw new ArgumentException($"{delta} should not be nested");
                }

            }
            return proto;
        }

        #endregion

        #region serialize ORSet.FullStateDeltaOp

        private byte[] ToBinary(ORSet<int>.FullStateDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<int>.FullStateDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary(ORSet<long>.FullStateDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<long>.FullStateDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary(ORSet<string>.FullStateDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<string>.FullStateDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary(ORSet<IActorRef>.FullStateDeltaOperation o) => ToProto(o).ToByteArray();
        private Proto.Msg.ORSet ToProto(ORSet<IActorRef>.FullStateDeltaOperation o) => ToProto(o.Underlying);
        private byte[] ToBinary<T>(ORSet<T>.FullStateDeltaOperation o) => ToProto<T>(o).ToByteArray();
        private Proto.Msg.ORSet ToProto<T>(ORSet<T>.FullStateDeltaOperation o) => ToProto<T>(o.Underlying);

        #endregion

        private byte[] ToBinary<T>(LWWRegister<T> o) => ToProto<T>(o).ToByteArray();
        private Proto.Msg.LWWRegister ToProto<T>(LWWRegister<T> o)
        {
            return new Proto.Msg.LWWRegister
            {
                TypeCode = _mappings[typeof(T)],
                Node = o.UpdatedBy.ToProto(),
                Timestamp = o.Timestamp,
                State = this.OtherMessageToProto(o.Value)
            };
        }

        #region serialize PNCounterDictionary

        private byte[] ToBinary(PNCounterDictionary<int> o) => ToProto(o).Compress();
        private Proto.Msg.PNCounterMap ToProto(PNCounterDictionary<int> o)
        {
            var proto = new Proto.Msg.PNCounterMap
            {
                KeyCode = _mappings[typeof(int)],
                Keys = ToProto(o.Underlying.KeySet)
            };

            foreach (var entry in o.Underlying.Entries)
            {
                proto.Entries.Add(new Proto.Msg.PNCounterMap.Types.Entry
                {
                    IntKey = entry.Key,
                    Value = ToProto(entry.Value)
                });
            }

            return proto;
        }

        private byte[] ToBinary(PNCounterDictionary<long> o) => ToProto(o).Compress();
        private Proto.Msg.PNCounterMap ToProto(PNCounterDictionary<long> o)
        {
            var proto = new Proto.Msg.PNCounterMap
            {
                KeyCode = _mappings[typeof(long)],
                Keys = ToProto(o.Underlying.KeySet)
            };

            foreach (var entry in o.Underlying.Entries)
            {
                proto.Entries.Add(new Proto.Msg.PNCounterMap.Types.Entry
                {
                    LongKey = entry.Key,
                    Value = ToProto(entry.Value)
                });
            }

            return proto;
        }

        private byte[] ToBinary(PNCounterDictionary<string> o) => ToProto(o).Compress();
        private Proto.Msg.PNCounterMap ToProto(PNCounterDictionary<string> o)
        {
            var proto = new Proto.Msg.PNCounterMap
            {
                KeyCode = _mappings[typeof(string)],
                Keys = ToProto(o.Underlying.KeySet)
            };

            foreach (var entry in o.Underlying.Entries)
            {
                proto.Entries.Add(new Proto.Msg.PNCounterMap.Types.Entry
                {
                    StringKey = entry.Key,
                    Value = ToProto(entry.Value)
                });
            }

            return proto;
        }

        private byte[] ToBinary<TKey>(PNCounterDictionary<TKey> o) => ToProto<TKey>(o).Compress();
        private Proto.Msg.PNCounterMap ToProto<TKey>(PNCounterDictionary<TKey> o)
        {
            var proto = new Proto.Msg.PNCounterMap
            {
                KeyCode = _mappings[typeof(TKey)],
                Keys = ToProto<TKey>(o.Underlying.KeySet)
            };

            foreach (var entry in o.Underlying.Entries)
            {
                proto.Entries.Add(new Proto.Msg.PNCounterMap.Types.Entry
                {
                    OtherKey = this.OtherMessageToProto(entry.Key),
                    Value = ToProto(entry.Value)
                });
            }

            return proto;
        }

        #endregion

        #region serialize ORDictionary 

        private byte[] ToBinary<TKey, TVal>(ORDictionary<TKey, TVal> o) where TVal : IReplicatedData<TVal> => ToProto<TKey, TVal>(o).Compress();
        private Proto.Msg.ORMap ToProto<TKey, TVal>(ORDictionary<TKey, TVal> o) where TVal : IReplicatedData<TVal>
        {
            dynamic keySet = o.KeySet;
            var proto = new Proto.Msg.ORMap
            {
                Keys = ToProto(keySet)
            };

            foreach (var entry in o.ValueMap)
            {
                dynamic key = entry.Key;
                object value = entry.Value;
                proto.Entries.Add(ToORDictionaryEntry(key, value));
            }

            return proto;
        }
        private Proto.Msg.ORMap.Types.Entry ToORDictionaryEntry(int key, object value) => new Proto.Msg.ORMap.Types.Entry
        {
            Value = this.OtherMessageToProto(value),
            IntKey = key
        };
        private Proto.Msg.ORMap.Types.Entry ToORDictionaryEntry(long key, object value) => new Proto.Msg.ORMap.Types.Entry
        {
            Value = this.OtherMessageToProto(value),
            LongKey = key
        };
        private Proto.Msg.ORMap.Types.Entry ToORDictionaryEntry(string key, object value) => new Proto.Msg.ORMap.Types.Entry
        {
            Value = this.OtherMessageToProto(value),
            StringKey = key
        };
        private Proto.Msg.ORMap.Types.Entry ToORDictionaryEntry<T>(T key, object value) => new Proto.Msg.ORMap.Types.Entry
        {
            Value = this.OtherMessageToProto(value),
            OtherKey = this.OtherMessageToProto(key)
        };

        #endregion

        #region serialize ORDictionary.PutDeltaOp

        private byte[] ToBinary<TVal>(ORDictionary<int, TVal>.PutDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<int, TVal>.IDeltaOperation)o).ToByteArray();

        private byte[] ToBinary<TVal>(ORDictionary<long, TVal>.PutDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<long, TVal>.IDeltaOperation)o).ToByteArray();

        private byte[] ToBinary<TVal>(ORDictionary<string, TVal>.PutDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<string, TVal>.IDeltaOperation)o).ToByteArray();

        private byte[] ToBinary<TKey, TVal>(ORDictionary<TKey, TVal>.PutDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<TKey, TVal>.IDeltaOperation)o).ToByteArray();

        #endregion

        #region serialize ORDictionary.RemoveDeltaOp

        private byte[] ToBinary<TVal>(ORDictionary<int, TVal>.RemoveDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<int, TVal>.IDeltaOperation)o).ToByteArray();
        private byte[] ToBinary<TVal>(ORDictionary<long, TVal>.RemoveDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<long, TVal>.IDeltaOperation)o).ToByteArray();
        private byte[] ToBinary<TVal>(ORDictionary<string, TVal>.RemoveDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<string, TVal>.IDeltaOperation)o).ToByteArray();
        private byte[] ToBinary<TKey, TVal>(ORDictionary<TKey, TVal>.RemoveDeltaOperation o) where TVal : IReplicatedData<TVal> => 
            ToProto((ORDictionary<TKey, TVal>.IDeltaOperation)o).ToByteArray();

        #endregion

        #region serialize ORDictionary.RemoveKeyDeltaOp

        private byte[] ToBinary<TVal>(ORDictionary<int, TVal>.RemoveKeyDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<int, TVal>.IDeltaOperation)o).ToByteArray();
        private byte[] ToBinary<TVal>(ORDictionary<long, TVal>.RemoveKeyDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<long, TVal>.IDeltaOperation)o).ToByteArray();
        private byte[] ToBinary<TVal>(ORDictionary<string, TVal>.RemoveKeyDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<string, TVal>.IDeltaOperation)o).ToByteArray();
        private byte[] ToBinary<TKey, TVal>(ORDictionary<TKey, TVal>.RemoveKeyDeltaOperation o) where TVal : IReplicatedData<TVal> => 
            ToProto((ORDictionary<TKey, TVal>.IDeltaOperation)o).ToByteArray();

        #endregion

        #region serialize ORDictionary.UpdateDeltaOp

        private byte[] ToBinary<TVal>(ORDictionary<int, TVal>.UpdateDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<int, TVal>.IDeltaOperation)o).ToByteArray();
        private byte[] ToBinary<TVal>(ORDictionary<long, TVal>.UpdateDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<long, TVal>.IDeltaOperation)o).ToByteArray();
        private byte[] ToBinary<TVal>(ORDictionary<string, TVal>.UpdateDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<string, TVal>.IDeltaOperation)o).ToByteArray();
        private byte[] ToBinary<TKey, TVal>(ORDictionary<TKey, TVal>.UpdateDeltaOperation o) where TVal : IReplicatedData<TVal> =>
            ToProto((ORDictionary<TKey, TVal>.IDeltaOperation)o).ToByteArray();

        #endregion

        #region serialize ORDictionary.GroupDeltaOp

        private byte[] ToBinary<TVal>(ORDictionary<int, TVal>.DeltaGroup o) where TVal : IReplicatedData<TVal> => ToProto(o.Operations).ToByteArray();
        private Proto.Msg.ORMapDeltaGroup ToProto<TVal>(params ORDictionary<int, TVal>.IDeltaOperation[] ops) where TVal : IReplicatedData<TVal>
        {
            var proto = new Proto.Msg.ORMapDeltaGroup();
            foreach (var op in ops)
            {
                Proto.Msg.ORMapDeltaGroup.Types.Entry entry = null;
                switch (op)
                {
                    case ORDictionary<int, TVal>.PutDeltaOperation put:
                        {
                            var u = (ORSet<int>.AddDeltaOperation)put.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapPut, u.Underlying);
                            entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                            {
                                IntKey = put.Key,
                                Value = this.OtherMessageToProto(put.Value)
                            });
                            break;
                        }
                    case ORDictionary<int, TVal>.RemoveDeltaOperation rem:
                        {
                            var u = (ORSet<int>.RemoveDeltaOperation)rem.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapRemove, u.Underlying);
                            break;
                        }
                    case ORDictionary<int, TVal>.RemoveKeyDeltaOperation remKey:
                        {
                            var u = (ORSet<int>.RemoveDeltaOperation)remKey.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapRemoveKey, u.Underlying);
                            entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                            {
                                IntKey = remKey.Key
                            });
                            break;
                        }
                    case ORDictionary<int, TVal>.UpdateDeltaOperation update:
                        {
                            var u = (ORSet<int>.AddDeltaOperation)update.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapUpdate, u.Underlying);
                            foreach (var e in update.Values)
                            {
                                entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                                {
                                    IntKey = e.Key,
                                    Value = this.OtherMessageToProto(e.Value)
                                });
                            }
                            break;
                        }
                    default: throw new ArgumentException($"{op} should not be nested");
                }
                proto.Entries.Add(entry);
            }

            return proto;
        }

        private byte[] ToBinary<TVal>(ORDictionary<long, TVal>.DeltaGroup o) where TVal : IReplicatedData<TVal> => ToProto(o.Operations).ToByteArray();
        private Proto.Msg.ORMapDeltaGroup ToProto<TVal>(params ORDictionary<long, TVal>.IDeltaOperation[] ops) where TVal : IReplicatedData<TVal>
        {
            var proto = new Proto.Msg.ORMapDeltaGroup();
            foreach (var op in ops)
            {
                Proto.Msg.ORMapDeltaGroup.Types.Entry entry = null;
                switch (op)
                {
                    case ORDictionary<long, TVal>.PutDeltaOperation put:
                        {
                            var u = (ORSet<long>.AddDeltaOperation)put.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapPut, u.Underlying);
                            entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                            {
                                LongKey = put.Key,
                                Value = this.OtherMessageToProto(put.Value)
                            });
                            break;
                        }
                    case ORDictionary<long, TVal>.RemoveDeltaOperation rem:
                        {
                            var u = (ORSet<long>.RemoveDeltaOperation)rem.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapRemove, u.Underlying);
                            break;
                        }
                    case ORDictionary<long, TVal>.RemoveKeyDeltaOperation remKey:
                        {
                            var u = (ORSet<long>.RemoveDeltaOperation)remKey.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapRemoveKey, u.Underlying);
                            entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                            {
                                LongKey = remKey.Key
                            });
                            break;
                        }
                    case ORDictionary<long, TVal>.UpdateDeltaOperation update:
                        {
                            var u = (ORSet<long>.AddDeltaOperation)update.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapUpdate, u.Underlying);
                            foreach (var e in update.Values)
                            {
                                entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                                {
                                    LongKey = e.Key,
                                    Value = this.OtherMessageToProto(e.Value)
                                });
                            }
                            break;
                        }
                    default: throw new ArgumentException($"{op} should not be nested");
                }
                proto.Entries.Add(entry);
            }

            return proto;
        }

        private byte[] ToBinary<TVal>(ORDictionary<string, TVal>.DeltaGroup o) where TVal : IReplicatedData<TVal> => ToProto(o.Operations).ToByteArray();
        private Proto.Msg.ORMapDeltaGroup ToProto<TVal>(params ORDictionary<string, TVal>.IDeltaOperation[] ops) where TVal : IReplicatedData<TVal>
        {
            var proto = new Proto.Msg.ORMapDeltaGroup();
            foreach (var op in ops)
            {
                Proto.Msg.ORMapDeltaGroup.Types.Entry entry = null;
                switch (op)
                {
                    case ORDictionary<string, TVal>.PutDeltaOperation put:
                        {
                            var u = (ORSet<string>.AddDeltaOperation)put.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapPut, u.Underlying);
                            entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                            {
                                StringKey = put.Key,
                                Value = this.OtherMessageToProto(put.Value)
                            });
                            break;
                        }
                    case ORDictionary<string, TVal>.RemoveDeltaOperation rem:
                        {
                            var u = (ORSet<string>.RemoveDeltaOperation)rem.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapRemove, u.Underlying);
                            break;
                        }
                    case ORDictionary<string, TVal>.RemoveKeyDeltaOperation remKey:
                        {
                            var u = (ORSet<string>.RemoveDeltaOperation)remKey.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapRemoveKey, u.Underlying);
                            entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                            {
                                StringKey = remKey.Key
                            });
                            break;
                        }
                    case ORDictionary<string, TVal>.UpdateDeltaOperation update:
                        {
                            var u = (ORSet<string>.AddDeltaOperation)update.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapUpdate, u.Underlying);
                            foreach (var e in update.Values)
                            {
                                entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                                {
                                    StringKey = e.Key,
                                    Value = this.OtherMessageToProto(e.Value)
                                });
                            }
                            break;
                        }
                    default: throw new ArgumentException($"{op} should not be nested");
                }
                proto.Entries.Add(entry);
            }

            return proto;
        }

        private byte[] ToBinary<TKey, TVal>(ORDictionary<TKey, TVal>.DeltaGroup o) where TVal : IReplicatedData<TVal> => ToProto(o.Operations).ToByteArray();
        private Proto.Msg.ORMapDeltaGroup ToProto<TKey, TVal>(params ORDictionary<TKey, TVal>.IDeltaOperation[] ops) where TVal : IReplicatedData<TVal>
        {
            var proto = new Proto.Msg.ORMapDeltaGroup();
            foreach (var op in ops)
            {
                Proto.Msg.ORMapDeltaGroup.Types.Entry entry = null;
                switch (op)
                {
                    case ORDictionary<TKey, TVal>.PutDeltaOperation put:
                        {
                            var u = (ORSet<TKey>.AddDeltaOperation)put.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapPut, u.Underlying);
                            entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                            {
                                OtherKey = this.OtherMessageToProto(put.Key),
                                Value = this.OtherMessageToProto(put.Value)
                            });
                            break;
                        }
                    case ORDictionary<TKey, TVal>.RemoveDeltaOperation rem:
                        {
                            var u = (ORSet<TKey>.RemoveDeltaOperation)rem.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapRemove, u.Underlying);
                            break;
                        }
                    case ORDictionary<TKey, TVal>.RemoveKeyDeltaOperation remKey:
                        {
                            var u = (ORSet<TKey>.RemoveDeltaOperation)remKey.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapRemoveKey, u.Underlying);
                            entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                            {
                                OtherKey = this.OtherMessageToProto(remKey.Key)
                            });
                            break;
                        }
                    case ORDictionary<TKey, TVal>.UpdateDeltaOperation update:
                        {
                            var u = (ORSet<TKey>.AddDeltaOperation)update.Underlying;
                            entry = CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp.OrmapUpdate, u.Underlying);
                            foreach (var e in update.Values)
                            {
                                entry.EntryData.Add(new Proto.Msg.ORMapDeltaGroup.Types.MapEntry
                                {
                                    OtherKey = this.OtherMessageToProto(e.Key),
                                    Value = this.OtherMessageToProto(e.Value)
                                });
                            }
                            break;
                        }
                    default: throw new ArgumentException($"{op} should not be nested");
                }
                proto.Entries.Add(entry);
            }

            return proto;
        }

        private Proto.Msg.ORMapDeltaGroup.Types.Entry CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp type, ORSet<int> delta)
        {
            var proto = new Proto.Msg.ORMapDeltaGroup.Types.Entry
            {
                Operation = type,
                Underlying = ToProto(delta)
            };
            return proto;
        }

        private Proto.Msg.ORMapDeltaGroup.Types.Entry CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp type, ORSet<long> delta)
        {
            var proto = new Proto.Msg.ORMapDeltaGroup.Types.Entry
            {
                Operation = type,
                Underlying = ToProto(delta)
            };
            return proto;
        }

        private Proto.Msg.ORMapDeltaGroup.Types.Entry CreateORMapDeltaEntry(Proto.Msg.ORMapDeltaOp type, ORSet<string> delta)
        {
            var proto = new Proto.Msg.ORMapDeltaGroup.Types.Entry
            {
                Operation = type,
                Underlying = ToProto(delta)
            };
            return proto;
        }

        private Proto.Msg.ORMapDeltaGroup.Types.Entry CreateORMapDeltaEntry<TKey>(Proto.Msg.ORMapDeltaOp type, ORSet<TKey> delta)
        {
            var proto = new Proto.Msg.ORMapDeltaGroup.Types.Entry
            {
                Operation = type,
                Underlying = ToProto(delta)
            };
            return proto;
        }

        #endregion

        #region serialize LWWDictionary

        private byte[] ToBinary<TVal>(LWWDictionary<int, TVal> o) => ToProto(o).Compress();
        private Proto.Msg.LWWMap ToProto<TVal>(LWWDictionary<int, TVal> o)
        {
            var proto = new Proto.Msg.LWWMap
            {
                Keys = ToProto(o.Underlying.KeySet)
            };

            foreach (var entry in o.Underlying.Entries)
                proto.Entries.Add(new Proto.Msg.LWWMap.Types.Entry
                {
                    Value = ToProto(entry.Value),
                    IntKey = entry.Key
                });

            return proto;
        }

        private byte[] ToBinary<TVal>(LWWDictionary<long, TVal> o) => ToProto(o).Compress();
        private Proto.Msg.LWWMap ToProto<TVal>(LWWDictionary<long, TVal> o)
        {
            var proto = new Proto.Msg.LWWMap
            {
                Keys = ToProto(o.Underlying.KeySet)
            };

            foreach (var entry in o.Underlying.Entries)
                proto.Entries.Add(new Proto.Msg.LWWMap.Types.Entry
                {
                    Value = ToProto(entry.Value),
                    LongKey = entry.Key
                });

            return proto;
        }

        private byte[] ToBinary<TVal>(LWWDictionary<string, TVal> o) => ToProto(o).Compress();
        private Proto.Msg.LWWMap ToProto<TVal>(LWWDictionary<string, TVal> o)
        {
            var proto = new Proto.Msg.LWWMap
            {
                Keys = ToProto(o.Underlying.KeySet)
            };

            foreach (var entry in o.Underlying.Entries)
                proto.Entries.Add(new Proto.Msg.LWWMap.Types.Entry
                {
                    Value = ToProto(entry.Value),
                    StringKey = entry.Key
                });

            return proto;
        }

        private byte[] ToBinary<TKey, TVal>(LWWDictionary<TKey, TVal> o) => ToProto(o).Compress();
        private Proto.Msg.LWWMap ToProto<TKey, TVal>(LWWDictionary<TKey, TVal> o)
        {
            var proto = new Proto.Msg.LWWMap
            {
                Keys = ToProto<TKey>(o.Underlying.KeySet)
            };

            foreach (var entry in o.Underlying.Entries)
                proto.Entries.Add(new Proto.Msg.LWWMap.Types.Entry
                {
                    Value = ToProto(entry.Value),
                    OtherKey = this.OtherMessageToProto(entry.Key)
                });

            return proto;
        }

        #endregion

        #region serialize ORMultiValueDictionary

        private byte[] ToBinary<TKey, TVal>(ORMultiValueDictionary<TKey, TVal> o) => ToProto(o).Compress();
        private Proto.Msg.ORMultiMap ToProto<TKey, TVal>(ORMultiValueDictionary<TKey, TVal> o)
        {
            dynamic keys = o.Underlying.KeySet;
            var proto = new Proto.Msg.ORMultiMap
            {
                Keys = ToProto(keys)
            };

            foreach (var entry in o.Underlying.Entries)
            {
                dynamic value = entry.Value;
                Proto.Msg.ORSet orset = ToProto(value);
                var e = new Proto.Msg.ORMultiMap.Types.Entry { Value = orset };
                switch ((object)entry.Key)
                {
                    case int i: e.IntKey = i; break;
                    case long l: e.LongKey = l; break;
                    case string s: e.StringKey = s; break;
                    default: e.OtherKey = this.OtherMessageToProto(entry.Key); break;
                }
                proto.Entries.Add(e);
            }

            if (o.DeltaValues)
                proto.WithValueDeltas = true;

            return proto;
        }

        #endregion

        private Proto.Msg.Flag ToProto(Flag o)
        {
            return new Proto.Msg.Flag
            {
                Enabled = o.Enabled
            };
        }

        private Proto.Msg.PNCounter ToProto(PNCounter o)
        {
            var proto = new Proto.Msg.PNCounter
            {
                Increments = ToProto(o.Increments),
                Decrements = ToProto(o.Decrements),
            };

            return proto;
        }

        private Proto.Msg.GCounter ToProto(GCounter o)
        {
            var proto = new Proto.Msg.GCounter();
            foreach (var entry in o.State)
            {
                proto.Entries.Add(new Proto.Msg.GCounter.Types.Entry
                {
                    Node = entry.Key.ToProto(),
                    Value = ByteString.CopyFrom(BitConverter.GetBytes(entry.Value))
                });
            }
            return proto;
        }

        public override object FromBinary(byte[] bytes, string manifest)
        {
            switch (manifest)
            {
                case DeletedDataManifest: return DeletedData.Instance;
                case GSetManifest: return FromProto(Proto.Msg.GSet.Parser.ParseFrom(bytes));
                case GSetKeyManifest: return KeyFromProto(Proto.Msg.Key.Parser.ParseFrom(bytes), typeof(GSetKey<>), 1);
                case ORSetManifest:
                {
                        return FromProto(Proto.Msg.ORSet.Parser.ParseFrom(bytes.Decompress()));
                }
                case ORSetKeyManifest: return KeyFromProto(Proto.Msg.Key.Parser.ParseFrom(bytes), typeof(ORSetKey<>), 1);
                case ORSetAddManifest: return ORSetAddFromProto(Proto.Msg.ORSet.Parser.ParseFrom(bytes));
                case ORSetRemoveManifest: return ORSetRemoveFromProto(Proto.Msg.ORSet.Parser.ParseFrom(bytes));
                case ORSetFullManifest: return ORSetFullFromProto(Proto.Msg.ORSet.Parser.ParseFrom(bytes));
                case ORSetDeltaGroupManifest: return FromProto(Proto.Msg.ORSetDeltaGroup.Parser.ParseFrom(bytes));
                case FlagManifest: return FromProto(Proto.Msg.Flag.Parser.ParseFrom(bytes));
                case FlagKeyManifest: return KeyFromProto(Proto.Msg.Key.Parser.ParseFrom(bytes), typeof(FlagKey), 0);
                case LWWRegisterManifest: return FromProto(Proto.Msg.LWWRegister.Parser.ParseFrom(bytes));
                case LWWRegisterKeyManifest: return KeyFromProto(Proto.Msg.Key.Parser.ParseFrom(bytes), typeof(LWWRegisterKey<>), 1);
                case GCounterManifest: return FromProto(Proto.Msg.GCounter.Parser.ParseFrom(bytes));
                case GCounterKeyManifest: return KeyFromProto(Proto.Msg.Key.Parser.ParseFrom(bytes), typeof(GCounterKey), 0);
                case PNCounterManifest: return FromProto(Proto.Msg.PNCounter.Parser.ParseFrom(bytes));
                case PNCounterKeyManifest: return KeyFromProto(Proto.Msg.Key.Parser.ParseFrom(bytes), typeof(PNCounterKey), 0);
                case ORMapManifest:
                {
                        return FromProto(Proto.Msg.ORMap.Parser.ParseFrom(bytes.Decompress()));
                }
                case ORMapKeyManifest: return KeyFromProto(Proto.Msg.Key.Parser.ParseFrom(bytes), typeof(ORDictionaryKey<,>), 2);
                case ORMapPutManifest: return OrMapPutFromProto(Proto.Msg.ORMapDeltaGroup.Parser.ParseFrom(bytes));
                case ORMapRemoveManifest: return OrMapRemoveFromProto(Proto.Msg.ORMapDeltaGroup.Parser.ParseFrom(bytes));
                case ORMapRemoveKeyManifest: return OrMapRemoveKeyFromProto(Proto.Msg.ORMapDeltaGroup.Parser.ParseFrom(bytes));
                case ORMapUpdateManifest: return OrMapUpdateFromProto(Proto.Msg.ORMapDeltaGroup.Parser.ParseFrom(bytes));
                case ORMapDeltaGroupManifest: return OrMapDeltaGroupFromProto(Proto.Msg.ORMapDeltaGroup.Parser.ParseFrom(bytes));
                case LWWMapManifest:
                {
                        return FromProto(Proto.Msg.LWWMap.Parser.ParseFrom(bytes.Decompress()));
                }
                case LWWMapKeyManifest: return KeyFromProto(Proto.Msg.Key.Parser.ParseFrom(bytes), typeof(LWWDictionaryKey<,>), 2);
                case PNCounterMapManifest:
                {
                        return FromProto(Proto.Msg.PNCounterMap.Parser.ParseFrom(bytes.Decompress()));
                }
                case PNCounterMapKeyManifest: return KeyFromProto(Proto.Msg.Key.Parser.ParseFrom(bytes), typeof(PNCounterDictionaryKey<>), 1);
                case ORMultiMapManifest:
                {
                        return FromProto(Proto.Msg.ORMultiMap.Parser.ParseFrom(bytes.Decompress()));
                }
                case ORMultiMapKeyManifest: return KeyFromProto(Proto.Msg.Key.Parser.ParseFrom(bytes), typeof(ORMultiValueDictionaryKey<,>), 2);
                case VersionVectorManifest: return this.VersionVectorFromProto(Proto.Msg.VersionVector.Parser.ParseFrom(bytes));
                default: throw new NotSupportedException($"Unimplemented deserialization of message with manifest [{manifest}] in [{GetType().FullName}]");
            }
        }

        private object KeyFromProto(Proto.Msg.Key proto, Type destinationType, int arity)
        {
            var path = proto.Path.ToStringUtf8();
            switch (arity)
            {
                case 1:
                    return Activator.CreateInstance(destinationType.MakeGenericType(_mappings[proto.ValueCode]), path);
                case 2:
                    var type = destinationType.MakeGenericType(_mappings[proto.KeyCode], _mappings[proto.ValueCode]);
                    return Activator.CreateInstance(type, path);
                default:
                    return Activator.CreateInstance(destinationType, path);
            }
        }
        
        private object OrMapDeltaGroupFromProto(Proto.Msg.ORMapDeltaGroup proto)
        {
            throw new NotImplementedException();
        }

        private object OrMapUpdateFromProto(Proto.Msg.ORMapDeltaGroup proto)
        {
            throw new NotImplementedException();
        }
        
        private object OrMapRemoveKeyFromProto(Proto.Msg.ORMapDeltaGroup proto)
        {
            throw new NotImplementedException();
        }

        private object OrMapRemoveFromProto(Proto.Msg.ORMapDeltaGroup proto)
        {
            throw new NotImplementedException();
        }

        private object OrMapPutFromProto(Proto.Msg.ORMapDeltaGroup proto)
        {
            throw new NotImplementedException();
        }

        private object FromProto(Proto.Msg.LWWMap proto)
        {
            throw new NotImplementedException();
        }

        private object FromProto(Proto.Msg.ORMultiMap proto)
        {
            throw new NotImplementedException();
        }

        private object FromProto(Proto.Msg.PNCounterMap proto)
        {
            dynamic orset = FromProto(proto.Keys);
            return DynamicPNCounterDictionary(orset, proto.Entries);
        }

        private PNCounterDictionary<T> DynamicPNCounterDictionary<T>(ORSet<T> orset, IList<Proto.Msg.PNCounterMap.Types.Entry> entries)
        {
            dynamic keys = null;
            var values = new PNCounter[entries.Count];
            var i = 0;
            foreach (var entry in entries)
            {
                values[i] = FromProto(entry.Value);

                if (entry.StringKey != null)
                {
                    var key = entry.StringKey;
                    if (keys == null)
                        keys = new string[entries.Count];
                    keys[i] = key;
                }
                else if (entry.OtherKey != null)
                {
                    var key = entry.OtherKey;
                    if (keys == null)
                        keys = Array.CreateInstance(key.GetType(), entries.Count);
                    keys[i] = key;
                }
                else if (entry.LongKey != default(long))
                {
                    var key = entry.LongKey;
                    if (keys == null)
                        keys = new long[entries.Count];
                    keys[i] = key;
                }
                else if (entry.IntKey != default(int))
                {
                    var key = entry.IntKey;
                    if (keys == null)
                        keys = new int[entries.Count];
                    keys[i] = key;
                }

                i++;
            }

            var ormap = DynamicORDictionary(orset, keys, values);
            return new PNCounterDictionary<T>(ormap);
        }

        private object FromProto(Proto.Msg.ORMap proto)
        {
            dynamic orset = FromProto(proto.Keys);
            dynamic keys = null;
            dynamic values = null;
            var i = 0;
            foreach (var entry in proto.Entries)
            {
                var value = this.OtherMessageFromProto(entry.Value);
                if (values == null)
                    values = Array.CreateInstance(value.GetType(), proto.Entries.Count);
                values[i] = value;

                if (entry.StringKey != null)
                {
                    var key = entry.StringKey;
                    if (keys == null)
                        keys = new string[proto.Entries.Count];
                    keys[i] = key;
                }
                else if (entry.OtherKey != null)
                {
                    var key = entry.OtherKey;
                    if (keys == null)
                        keys = Array.CreateInstance(key.GetType(), proto.Entries.Count);
                    keys[i] = key;
                }
                else if (entry.LongKey != default(long))
                {
                    var key = entry.LongKey;
                    if (keys == null)
                        keys = new long[proto.Entries.Count];
                    keys[i] = key;
                }
                else if (entry.IntKey != default(int))
                {
                    var key = entry.IntKey;
                    if (keys == null)
                        keys = new int[proto.Entries.Count];
                    keys[i] = key;
                }

                i++;
            }
            return DynamicORDictionary(orset, keys, values);
        }

        private ORDictionary<TKey, TValue> DynamicORDictionary<TKey, TValue>(ORSet<TKey> keySet, TKey[] keys, TValue[] values)
            where TValue : IReplicatedData<TValue>
        {
            var builder = ImmutableDictionary<TKey, TValue>.Empty.ToBuilder();
            for (int i = 0; i < keys.Length; i++)
            {
                builder.Add(keys[i], values[i]);
            }
            
            return new ORDictionary<TKey,TValue>(keySet, builder.ToImmutable());
        }

        private PNCounter FromProto(Proto.Msg.PNCounter proto) => 
            new PNCounter(FromProto(proto.Increments), FromProto(proto.Decrements));

        private GCounter FromProto(Proto.Msg.GCounter proto)
        {
            var builder = ImmutableDictionary<Akka.Cluster.UniqueAddress, ulong>.Empty.ToBuilder();
            foreach (var entry in proto.Entries)
            {
                var node = this.UniqueAddressFromProto(entry.Node);
                var value = BitConverter.ToUInt64(entry.Value.ToByteArray(), 0);
                builder.Add(node, value);
            }
            return new GCounter(builder.ToImmutable());
        }

        private object FromProto(Proto.Msg.LWWRegister proto)
        {
            var node = this.UniqueAddressFromProto(proto.Node);
            dynamic state = this.OtherMessageFromProto(proto.State);
            return DynamicLWWRegister(node, state, proto.Timestamp);
        }

        private LWWRegister<T> DynamicLWWRegister<T>(Akka.Cluster.UniqueAddress node, T state, long timestamp)
        {
            return new LWWRegister<T>(node, state, timestamp);
        }

        private Flag FromProto(Proto.Msg.Flag proto) => proto.Enabled ? Flag.True : Flag.False;

        #region deserialize ORSet
        
        private object FromProto(Proto.Msg.ORSetDeltaGroup proto)
        {
            var head = proto.Entries[0];
            dynamic orset = FromProto(head.Underlying);
            return DynamicORSetDelta(orset, head.Operation, proto);
        }

        private ORSet<T>.DeltaGroup DynamicORSetDelta<T>(ORSet<T> horset, Proto.Msg.ORSetDeltaOp hop, Proto.Msg.ORSetDeltaGroup proto)
        {
            var builder = ImmutableArray.CreateBuilder<IReplicatedData>(proto.Entries.Count);
            var op = ORSetDeltaFrom(hop, horset);
            builder.Add(op);

            for (int i = 1; i < proto.Entries.Count; i++)
            {
                var entry = proto.Entries[i];
                var orset = (ORSet<T>)FromProto(entry.Underlying);
                builder.Add(ORSetDeltaFrom(entry.Operation, orset));
            }
            
            return new ORSet<T>.DeltaGroup(builder.ToImmutable());
        }

        private ORSet<T>.IDeltaOperation ORSetDeltaFrom<T>(Proto.Msg.ORSetDeltaOp op, ORSet<T> orset)
        {
            switch (op)
            {
                case Proto.Msg.ORSetDeltaOp.Add: return DynamicORSetAdd(orset);
                case Proto.Msg.ORSetDeltaOp.Remove: return DynamicORSetRemove(orset);
                case Proto.Msg.ORSetDeltaOp.Full: return DynamicORSetFull(orset);
                default: throw new ArgumentException("Delta operation cannot be nested");
            }
        }

        private object ORSetFullFromProto(Proto.Msg.ORSet proto)
        {
            dynamic orset = FromProto(proto);
            return DynamicORSetFull(orset);
        }

        private ORSet<T>.FullStateDeltaOperation DynamicORSetFull<T>(ORSet<T> orset) =>
            new ORSet<T>.FullStateDeltaOperation(orset);

        private object ORSetRemoveFromProto(Proto.Msg.ORSet proto)
        {
            dynamic orset = FromProto(proto);
            return DynamicORSetRemove(orset);
        }

        private ORSet<T>.RemoveDeltaOperation DynamicORSetRemove<T>(ORSet<T> orset) =>
            new ORSet<T>.RemoveDeltaOperation(orset);

        private object ORSetAddFromProto(Proto.Msg.ORSet proto)
        {
            dynamic orset = FromProto(proto);
            return DynamicORSetAdd(orset);
        }

        private ORSet<T>.AddDeltaOperation DynamicORSetAdd<T>(ORSet<T> orset) => 
            new ORSet<T>.AddDeltaOperation(orset);

        private object FromProto(Proto.Msg.ORSet proto)
        {
            VersionVector vvector = null;
            List<VersionVector> dots = null;

            if (!(proto.Vvector is null))
            {
                vvector = this.VersionVectorFromProto(proto.Vvector);
            }

            if (!(proto.Dots is null))
            {
                dots = new List<VersionVector>(proto.Dots.Count);
                foreach (var dot in proto.Dots)
                    dots.Add(this.VersionVectorFromProto(dot));
            }

            if (proto.IntElements.Count != 0)
            {
                var elements =
                    proto.IntElements.Zip(dots, (value, dot) => new KeyValuePair<int, VersionVector>(value, dot))
                    .ToImmutableDictionary();
                
                return new ORSet<int>(elements, vvector);
            }
            else if (proto.LongElements.Count != 0)
            {
                var elements =
                    proto.LongElements.Zip(dots, (value, dot) => new KeyValuePair<long, VersionVector>(value, dot))
                        .ToImmutableDictionary();

                return new ORSet<long>(elements, vvector);
            }
            else if (proto.StringElements.Count != 0)
            {
                var elements =
                    proto.StringElements.Zip(dots, (value, dot) => new KeyValuePair<string, VersionVector>(value, dot))
                        .ToImmutableDictionary();

                return new ORSet<string>(elements, vvector);
            }
            else if (proto.ActorRefElements.Count != 0)
            {
                var elements =
                    proto.ActorRefElements.Zip(dots, (value, dot) => 
                            new KeyValuePair<IActorRef, VersionVector>(system.Provider.ResolveActorRef(value), dot))
                        .ToImmutableDictionary();

                return new ORSet<IActorRef>(elements, vvector);
            }
            else
            {
                // we'll use it only as type marker for dynamic binding
                dynamic marker = Array.CreateInstance(_mappings[proto.TypeCode], 0);
                return DynamicORSet(marker, proto, dots, vvector);
            }
        }

        private ORSet<T> DynamicORSet<T>(T[] marker, Proto.Msg.ORSet proto, List<VersionVector> dots, VersionVector vvector)
        {
            if (proto.OtherElements.Count == 0 && ReferenceEquals(vvector, VersionVector.Empty)) return ORSet<T>.Empty;

            var builder = ImmutableDictionary.CreateBuilder<T, VersionVector>();
            int i = 0;
            foreach (var other in proto.OtherElements)
            {
                var dot = dots[i];
                var value = (T)this.OtherMessageFromProto(other);
                builder[value] = dot;
                i++;
            }
            return new ORSet<T>(builder.ToImmutable(), vvector);
        }

        #endregion

        #region deserialize GSet

        private object FromProto(Proto.Msg.GSet proto)
        {
            if (proto.IntElements.Count != 0)
            {
                var count = proto.IntElements.Count;
                var elements = new int[count];
                var i = 0;
                foreach (var item in proto.IntElements)
                {
                    elements[i] = item;
                    i++;
                }
                return GSet.Create(elements);
            }
            else if (proto.LongElements.Count != 0)
            {
                var count = proto.LongElements.Count;
                var elements = new long[count];
                var i = 0;
                foreach (var item in proto.LongElements)
                {
                    elements[i] = item;
                    i++;
                }
                return GSet.Create(elements);
            }
            else if (proto.StringElements.Count != 0)
            {
                var count = proto.StringElements.Count;
                var elements = new string[count];
                var i = 0;
                foreach (var item in proto.StringElements)
                {
                    elements[i] = item;
                    i++;
                }
                return GSet.Create(elements);

            }
            else if (proto.ActorRefElements.Count != 0)
            {
                var count = proto.ActorRefElements.Count;
                var elements = new IActorRef[count];
                var i = 0;
                foreach (var item in proto.ActorRefElements)
                {
                    elements[i] = system.Provider.ResolveActorRef(item);
                    i++;
                }
                return GSet.Create(elements);
            }
            else
            {
                dynamic elements = Array.CreateInstance(_mappings[proto.TypeCode], proto.OtherElements.Count);
                return DynamicGSet(elements, proto);
            }
        }

        private GSet<T> DynamicGSet<T>(T[] elements, Proto.Msg.GSet proto)
        {
            if (elements.Length == 0)
            {
                return GSet<T>.Empty;
            }
            else
            {
                var i = 0;
                foreach (var element in proto.OtherElements)
                {
                    elements[i++] = (T)this.OtherMessageFromProto(element);
                }

                return GSet.Create<T>(elements);
            }
        }

        #endregion
    }
}
