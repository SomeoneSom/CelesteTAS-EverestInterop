using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace StudioCommunication;

public static class BinaryHelper {
    // Serializes data to (usually) a binary buffer for transmission
    // A buffer length of 0 indicates that the object is null
    // Some primitive types are special cased, to avoid the overhead of the buffer
    
    public static void WriteObject(this BinaryWriter writer, object? value) {
        switch (value)
        {
            // Primitives
            case bool v:
                writer.Write(v);
                return;
            case byte v:
                writer.Write(v);
                return;
            case byte[] v:
                writer.Write7BitEncodedInt(v.Length);
                writer.Write(v);
                return;
            case char v:
                writer.Write(v);
                return;
            case char[] v:
                writer.Write7BitEncodedInt(v.Length);
                writer.Write(v);
                return;
            case decimal v:
                writer.Write(v);
                return;
            case double v:
                writer.Write(v);
                return;
            case float v:
                writer.Write(v);
                return;
            case int v:
                writer.Write(v);
                return;
            case long v:
                writer.Write(v);
                return;
            case sbyte v:
                writer.Write(v);
                return;
            case short v:
                writer.Write(v);
                return;
            case Half v:
                writer.Write(v);
                return;
            case string v:
                writer.Write(v);
                return;
            
            case ITuple v:
                writer.Write7BitEncodedInt(v.Length);
                for (int i = 0; i < v.Length; i++) {
                    writer.WriteObject(v[i]);
                }
                return;
        }
        
        if (value == null) {
            writer.Write7BitEncodedInt(0);
            return;
        }
        
        var buffer = MemoryPackSerializer.Serialize(value.GetType(), value);
        writer.Write7BitEncodedInt(buffer.Length);
        writer.Write(buffer);
    }

    public static T ReadObject<T>(this BinaryReader reader) => (T)reader.ReadObject(typeof(T));
    public static object ReadObject(this BinaryReader reader, Type type) {
        // Primitives
        if (type == typeof(bool))
            return reader.ReadBoolean();
        if (type == typeof(byte))
            return reader.ReadByte();
        if (type == typeof(byte[]))
            return reader.ReadBytes(reader.Read7BitEncodedInt());
        if (type == typeof(char))
            return reader.ReadChar();
        if (type == typeof(char[]))
            return reader.ReadChars(reader.Read7BitEncodedInt());
        if (type == typeof(decimal))
            return reader.ReadDecimal();
        if (type == typeof(double))
            return reader.ReadDouble();
        if (type == typeof(float))
            return reader.ReadSingle();
        if (type == typeof(int))
            return reader.ReadInt32();
        if (type == typeof(long))
            return reader.ReadInt64();
        if (type == typeof(sbyte))
            return reader.ReadSByte();
        if (type == typeof(short))
            return reader.ReadInt16();
        if (type == typeof(Half))
            return reader.ReadHalf();
        if (type == typeof(string))
            return reader.ReadString();
        
        if (type.IsAssignableTo(typeof(ITuple)) && type.IsGenericType) {
            int count = reader.Read7BitEncodedInt();

            var itemType = type.GenericTypeArguments[0];
            var values = Array.CreateInstance(itemType, count);
            for (int i = 0; i < count; i++) {
                values.SetValue(reader.ReadObject(itemType), i);
            }

            return GetTuple(values, itemType);
        }
        
        int length = reader.Read7BitEncodedInt();
        var buffer = reader.ReadBytes(length);
        return MemoryPackSerializer.Deserialize(type, buffer)!;
    }
    
    private static object GetTuple(Array values, Type itemType) {
        var typeArgs = new Type[values.Length];
        Array.Fill(typeArgs, itemType);
        
        var specificType = Type.GetType("System.Tuple`" + values.Length)!.MakeGenericType(typeArgs);
        var constructorArguments = values.Cast<object>().ToArray();

        return Activator.CreateInstance(specificType, constructorArguments);
    }
}