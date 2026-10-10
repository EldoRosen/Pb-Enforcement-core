using System;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using Torch.Collections;

namespace Advanced_PB_Limiter.Utils
{
    public class MtObservableSortedSerializableDictionary<TK, TV> : MtObservableSortedDictionary<TK, TV>, IXmlSerializable
    {
        public XmlSchema? GetSchema()
        {
            return null;
        }

        public void ReadXml(XmlReader reader)
        {
            if (reader.IsEmptyElement)
            {
                reader.ReadStartElement();
                return;
            }

            reader.ReadStartElement();
            XmlSerializer keySerializer = new (typeof(TK));
            XmlSerializer valueSerializer = new (typeof(TV));

            while (reader.NodeType != XmlNodeType.EndElement)
            {
                reader.ReadStartElement("Item");

                reader.ReadStartElement("Key");
                TK? key = reader.IsEmptyElement ? default : (TK)keySerializer.Deserialize(reader);
                reader.ReadEndElement(); // Key

                reader.ReadStartElement("Value");
                TV? value = reader.IsEmptyElement ? default : (TV)valueSerializer.Deserialize(reader);
                reader.ReadEndElement(); // Value

                if (key is null)
                    throw new ArgumentNullException(nameof(key), @"The key cannot be null.");
                if (value is null)
                    throw new ArgumentNullException(nameof(value), @"The value cannot be null.");
                
                Add(key, value);

                reader.ReadEndElement(); // Item
                reader.MoveToContent();
            }
            reader.ReadEndElement();
        }

        public void WriteXml(XmlWriter writer)
        {
            XmlSerializer keySerializer = new (typeof(TK));
            XmlSerializer valueSerializer = new (typeof(TV));

            foreach (KeyValuePair<TK, TV> kvp in this)
            {
                writer.WriteStartElement("Item");

                writer.WriteStartElement("Key");
                if (kvp.Key != null)
                {
                    keySerializer.Serialize(writer, kvp.Key);
                }
                writer.WriteEndElement(); // Key

                writer.WriteStartElement("Value");
                if (kvp.Value != null)
                {
                    valueSerializer.Serialize(writer, kvp.Value);
                }
                writer.WriteEndElement(); // Value

                writer.WriteEndElement(); // Item
            }
        }
    }
}