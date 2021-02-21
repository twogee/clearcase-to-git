using System;
using System.Collections.Generic;
using ProtoBuf;

namespace GitImporter
{
    [Serializable]
    [ProtoContract]
    public class SymLinkElement : Element
    {
        public const string Symlink = "symlink:";

        public Element Directory { get; private set; }
        [ProtoMember(1, AsReference = true)] private string _directoryOid;

        [ProtoMember(2, AsReference = true)]
        public string Target { get; private set; }

        public SymLinkElement(Element directory, string name)
            : base(directory + "\\" + name.Substring(Symlink.Length), false)
        {
            Oid = Symlink + Name;
            Directory = directory;
            Target = name.Substring(Symlink.Length);
        }

        // for Protobuf deserialization
        public SymLinkElement()
        {}

        [ProtoBeforeSerialization]
        private void BeforeProtobufSerialization()
        {
            _directoryOid = Directory.Oid;
        }

        public void Fixup(Dictionary<string, Element> elementsByOid)
        {
            Directory = elementsByOid[_directoryOid];
        }
    }
}
