﻿#region Copyright notice and license
// Protocol Buffers - Google's data interchange format
// Copyright 2015 Google Inc.  All rights reserved.
// https://developers.google.com/protocol-buffers/
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
//     * Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the
// distribution.
//     * Neither the name of Google Inc. nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
#endregion

using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Google.Protobuf
{
    /// <summary>
    /// Reflection-based converter from JSON to messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instances of this class are thread-safe, with no mutable state.
    /// </para>
    /// <para>
    /// This is a simple start to get JSON parsing working. As it's reflection-based,
    /// it's not as quick as baking calls into generated messages - but is a simpler implementation.
    /// (This code is generally not heavily optimized.)
    /// </para>
    /// </remarks>
    public sealed class JsonParser
    {
        // Note: using 0-9 instead of \d to ensure no non-ASCII digits.
        // This regex isn't a complete validator, but will remove *most* invalid input. We rely on parsing to do the rest.
        private static readonly Regex TimestampRegex = new Regex(@"^(?<datetime>[0-9]{4}-[01][0-9]-[0-3][0-9]T[012][0-9]:[0-5][0-9]:[0-5][0-9])(?<subseconds>\.[0-9]{1,9})?(?<offset>(Z|[+-][0-1][0-9]:[0-5][0-9]))$", FrameworkPortability.CompiledRegexWhereAvailable);
        private static readonly Regex DurationRegex = new Regex(@"^(?<sign>-)?(?<int>[0-9]{1,12})(?<subseconds>\.[0-9]{1,9})?s$", FrameworkPortability.CompiledRegexWhereAvailable);
        private static readonly int[] SubsecondScalingFactors = { 0, 100000000, 100000000, 10000000, 1000000, 100000, 10000, 1000, 100, 10, 1 };
        private static readonly char[] FieldMaskPathSeparators = new[] { ',' };

        private static readonly JsonParser defaultInstance = new JsonParser(Settings.Default);

        private static readonly Dictionary<string, Action<JsonParser, IMessage, JsonTokenizer>>
            WellKnownTypeHandlers = new Dictionary<string, Action<JsonParser, IMessage, JsonTokenizer>>
        {
            { Timestamp.Descriptor.FullName, (parser, message, tokenizer) => MergeTimestamp(message, tokenizer.Next()) },
            { Duration.Descriptor.FullName, (parser, message, tokenizer) => MergeDuration(message, tokenizer.Next()) },
            { Value.Descriptor.FullName, (parser, message, tokenizer) => parser.MergeStructValue(message, tokenizer) },
            { ListValue.Descriptor.FullName, (parser, message, tokenizer) => parser.MergeRepeatedField(message, message.Descriptor.Fields[ListValue.ValuesFieldNumber], tokenizer) },
            { Struct.Descriptor.FullName, (parser, message, tokenizer) => parser.MergeStruct(message, tokenizer) },
            { FieldMask.Descriptor.FullName, (parser, message, tokenizer) => MergeFieldMask(message, tokenizer.Next()) },
            { Int32Value.Descriptor.FullName, MergeWrapperField },
            { Int64Value.Descriptor.FullName, MergeWrapperField },
            { UInt32Value.Descriptor.FullName, MergeWrapperField },
            { UInt64Value.Descriptor.FullName, MergeWrapperField },
            { FloatValue.Descriptor.FullName, MergeWrapperField },
            { DoubleValue.Descriptor.FullName, MergeWrapperField },
            { BytesValue.Descriptor.FullName, MergeWrapperField },
            { StringValue.Descriptor.FullName, MergeWrapperField }
        };

        // Convenience method to avoid having to repeat the same code multiple times in the above
        // dictionary initialization.
        private static void MergeWrapperField(JsonParser parser, IMessage message, JsonTokenizer tokenizer)
        {
            parser.MergeField(message, message.Descriptor.Fields[Wrappers.WrapperValueFieldNumber], tokenizer);
        }

        /// <summary>
        /// Returns a formatter using the default settings.        /// </summary>
        public static JsonParser Default { get { return defaultInstance; } }

// Currently the settings are unused.
// TODO: When we've implemented Any (and the json spec is finalized), revisit whether they're
// needed at all.
#pragma warning disable 0414
        private readonly Settings settings;
#pragma warning restore 0414

        /// <summary>
        /// Creates a new formatted with the given settings.
        /// </summary>
        /// <param name="settings">The settings.</param>
        public JsonParser(Settings settings)
        {
            this.settings = settings;
        }

        /// <summary>
        /// Parses <paramref name="json"/> and merges the information into the given message.
        /// </summary>
        /// <param name="message">The message to merge the JSON information into.</param>
        /// <param name="json">The JSON to parse.</param>
        internal void Merge(IMessage message, string json)
        {
            Merge(message, new StringReader(json));
        }

        /// <summary>
        /// Parses JSON read from <paramref name="jsonReader"/> and merges the information into the given message.
        /// </summary>
        /// <param name="message">The message to merge the JSON information into.</param>
        /// <param name="jsonReader">Reader providing the JSON to parse.</param>
        internal void Merge(IMessage message, TextReader jsonReader)
        {
            var tokenizer = new JsonTokenizer(jsonReader);
            Merge(message, tokenizer);
            var lastToken = tokenizer.Next();
            if (lastToken != JsonToken.EndDocument)
            {
                throw new InvalidProtocolBufferException("Expected end of JSON after object");
            }
        }

        /// <summary>
        /// Merges the given message using data from the given tokenizer. In most cases, the next
        /// token should be a "start object" token, but wrapper types and nullity can invalidate
        /// that assumption. This is implemented as an LL(1) recursive descent parser over the stream
        /// of tokens provided by the tokenizer. This token stream is assumed to be valid JSON, with the
        /// tokenizer performing that validation - but not every token stream is valid "protobuf JSON".
        /// </summary>
        private void Merge(IMessage message, JsonTokenizer tokenizer)
        {
            if (message.Descriptor.IsWellKnownType)
            {
                Action<JsonParser, IMessage, JsonTokenizer> handler;
                if (WellKnownTypeHandlers.TryGetValue(message.Descriptor.FullName, out handler))
                {
                    handler(this, message, tokenizer);
                    return;
                }
                // Well-known types with no special handling continue in the normal way.
            }
            var token = tokenizer.Next();
            if (token.Type != JsonToken.TokenType.StartObject)
            {
                throw new InvalidProtocolBufferException("Expected an object");
            }
            var descriptor = message.Descriptor;
            var jsonFieldMap = descriptor.Fields.ByJsonName();
            while (true)
            {
                token = tokenizer.Next();
                if (token.Type == JsonToken.TokenType.EndObject)
                {
                    return;
                }
                if (token.Type != JsonToken.TokenType.Name)
                {
                    throw new InvalidOperationException("Unexpected token type " + token.Type);
                }
                string name = token.StringValue;
                FieldDescriptor field;
                if (jsonFieldMap.TryGetValue(name, out field))
                {
                    MergeField(message, field, tokenizer);
                }
                else
                {
                    // TODO: Is this what we want to do? If not, we'll need to skip the value,
                    // which may be an object or array. (We might want to put code in the tokenizer
                    // to do that.)
                    throw new InvalidProtocolBufferException("Unknown field: " + name);
                }
            }
        }

        private void MergeField(IMessage message, FieldDescriptor field, JsonTokenizer tokenizer)
        {
            var token = tokenizer.Next();
            if (token.Type == JsonToken.TokenType.Null)
            {
                // Note: different from Java API, which just ignores it.
                // TODO: Bring it more in line? Discuss...
                field.Accessor.Clear(message);
                return;
            }
            tokenizer.PushBack(token);

            if (field.IsMap)
            {
                MergeMapField(message, field, tokenizer);
            }
            else if (field.IsRepeated)
            {
                MergeRepeatedField(message, field, tokenizer);
            }
            else
            {
                var value = ParseSingleValue(field, tokenizer);
                field.Accessor.SetValue(message, value);
            }
        }

        private void MergeRepeatedField(IMessage message, FieldDescriptor field, JsonTokenizer tokenizer)
        {
            var token = tokenizer.Next();
            if (token.Type != JsonToken.TokenType.StartArray)
            {
                throw new InvalidProtocolBufferException("Repeated field value was not an array. Token type: " + token.Type);
            }

            IList list = (IList) field.Accessor.GetValue(message);
            while (true)
            {
                token = tokenizer.Next();
                if (token.Type == JsonToken.TokenType.EndArray)
                {
                    return;
                }
                tokenizer.PushBack(token);
                list.Add(ParseSingleValue(field, tokenizer));
            }
        }

        private void MergeMapField(IMessage message, FieldDescriptor field, JsonTokenizer tokenizer)
        {
            // Map fields are always objects, even if the values are well-known types: ParseSingleValue handles those.
            var token = tokenizer.Next();
            if (token.Type != JsonToken.TokenType.StartObject)
            {
                throw new InvalidProtocolBufferException("Expected an object to populate a map");
            }

            var type = field.MessageType;
            var keyField = type.FindFieldByNumber(1);
            var valueField = type.FindFieldByNumber(2);
            if (keyField == null || valueField == null)
            {
                throw new InvalidProtocolBufferException("Invalid map field: " + field.FullName);
            }
            IDictionary dictionary = (IDictionary) field.Accessor.GetValue(message);

            while (true)
            {
                token = tokenizer.Next();
                if (token.Type == JsonToken.TokenType.EndObject)
                {
                    return;
                }
                object key = ParseMapKey(keyField, token.StringValue);
                object value = ParseSingleValue(valueField, tokenizer);
                // TODO: Null handling
                dictionary[key] = value;
            }
        }

        private object ParseSingleValue(FieldDescriptor field, JsonTokenizer tokenizer)
        {
            var token = tokenizer.Next();
            if (token.Type == JsonToken.TokenType.Null)
            {
                if (field.FieldType == FieldType.Message && field.MessageType.FullName == Value.Descriptor.FullName)
                {
                    return new Value { NullValue = NullValue.NULL_VALUE };
                }
                return null;
            }

            var fieldType = field.FieldType;
            if (fieldType == FieldType.Message)
            {
                // Parse wrapper types as their constituent types.
                // TODO: What does this mean for null?
                // TODO: Detect this differently when we have dynamic messages, and put it in one place...
                if (field.MessageType.IsWellKnownType && field.MessageType.File == Int32Value.Descriptor.File)
                {
                    field = field.MessageType.Fields[Wrappers.WrapperValueFieldNumber];
                    fieldType = field.FieldType;
                }
                else
                {
                    // TODO: Merge the current value in message? (Public API currently doesn't make this relevant as we don't expose merging.)
                    tokenizer.PushBack(token);
                    IMessage subMessage = NewMessageForField(field);
                    Merge(subMessage, tokenizer);
                    return subMessage;
                }
            }

            switch (token.Type)
            {
                case JsonToken.TokenType.True:
                case JsonToken.TokenType.False:
                    if (fieldType == FieldType.Bool)
                    {
                        return token.Type == JsonToken.TokenType.True;
                    }
                    // Fall through to "we don't support this type for this case"; could duplicate the behaviour of the default
                    // case instead, but this way we'd only need to change one place.
                    goto default;
                case JsonToken.TokenType.StringValue:
                    return ParseSingleStringValue(field, token.StringValue);
                // Note: not passing the number value itself here, as we may end up storing the string value in the token too.
                case JsonToken.TokenType.Number:
                    return ParseSingleNumberValue(field, token);
                case JsonToken.TokenType.Null:
                    throw new NotImplementedException("Haven't worked out what to do for null yet");
                default:
                    throw new InvalidProtocolBufferException("Unsupported JSON token type " + token.Type + " for field type " + fieldType);
            }
        }

        /// <summary>
        /// Parses <paramref name="json"/> into a new message.
        /// </summary>
        /// <typeparam name="T">The type of message to create.</typeparam>
        /// <param name="json">The JSON to parse.</param>
        /// <exception cref="InvalidJsonException">The JSON does not comply with RFC 7159</exception>
        /// <exception cref="InvalidProtocolBufferException">The JSON does not represent a Protocol Buffers message correctly</exception>
        public T Parse<T>(string json) where T : IMessage, new()
        {
            return Parse<T>(new StringReader(json));
        }

        /// <summary>
        /// Parses JSON read from <paramref name="jsonReader"/> into a new message.
        /// </summary>
        /// <typeparam name="T">The type of message to create.</typeparam>
        /// <param name="jsonReader">Reader providing the JSON to parse.</param>
        /// <exception cref="InvalidJsonException">The JSON does not comply with RFC 7159</exception>
        /// <exception cref="InvalidProtocolBufferException">The JSON does not represent a Protocol Buffers message correctly</exception>
        public T Parse<T>(TextReader jsonReader) where T : IMessage, new()
        {
            T message = new T();
            Merge(message, jsonReader);
            return message;
        }

        private void MergeStructValue(IMessage message, JsonTokenizer tokenizer)
        {
            var firstToken = tokenizer.Next();
            var fields = message.Descriptor.Fields;
            switch (firstToken.Type)
            {
                case JsonToken.TokenType.Null:
                    fields[Value.NullValueFieldNumber].Accessor.SetValue(message, 0);
                    return;
                case JsonToken.TokenType.StringValue:
                    fields[Value.StringValueFieldNumber].Accessor.SetValue(message, firstToken.StringValue);
                    return;
                case JsonToken.TokenType.Number:
                    fields[Value.NumberValueFieldNumber].Accessor.SetValue(message, firstToken.NumberValue);
                    return;
                case JsonToken.TokenType.False:
                case JsonToken.TokenType.True:
                    fields[Value.BoolValueFieldNumber].Accessor.SetValue(message, firstToken.Type == JsonToken.TokenType.True);
                    return;
                case JsonToken.TokenType.StartObject:
                    {
                        var field = fields[Value.StructValueFieldNumber];
                        var structMessage = NewMessageForField(field);
                        tokenizer.PushBack(firstToken);
                        Merge(structMessage, tokenizer);
                        field.Accessor.SetValue(message, structMessage);
                        return;
                    }
                case JsonToken.TokenType.StartArray:
                    {
                        var field = fields[Value.ListValueFieldNumber];
                        var list = NewMessageForField(field);
                        tokenizer.PushBack(firstToken);
                        Merge(list, tokenizer);
                        field.Accessor.SetValue(message, list);
                        return;
                    }
                default:
                    throw new InvalidOperationException("Unexpected token type: " + firstToken.Type);
            }
        }

        private void MergeStruct(IMessage message, JsonTokenizer tokenizer)
        {
            var token = tokenizer.Next();
            if (token.Type != JsonToken.TokenType.StartObject)
            {
                throw new InvalidProtocolBufferException("Expected object value for Struct");
            }
            tokenizer.PushBack(token);

            var field = message.Descriptor.Fields[Struct.FieldsFieldNumber];
            MergeMapField(message, field, tokenizer);
        }

        #region Utility methods which don't depend on the state (or settings) of the parser.
        private static object ParseMapKey(FieldDescriptor field, string keyText)
        {
            switch (field.FieldType)
            {
                case FieldType.Bool:
                    if (keyText == "true")
                    {
                        return true;
                    }
                    if (keyText == "false")
                    {
                        return false;
                    }
                    throw new InvalidProtocolBufferException("Invalid string for bool map key: " + keyText);
                case FieldType.String:
                    return keyText;
                case FieldType.Int32:
                case FieldType.SInt32:
                case FieldType.SFixed32:
                    return ParseNumericString(keyText, int.Parse, false);
                case FieldType.UInt32:
                case FieldType.Fixed32:
                    return ParseNumericString(keyText, uint.Parse, false);
                case FieldType.Int64:
                case FieldType.SInt64:
                case FieldType.SFixed64:
                    return ParseNumericString(keyText, long.Parse, false);
                case FieldType.UInt64:
                case FieldType.Fixed64:
                    return ParseNumericString(keyText, ulong.Parse, false);
                default:
                    throw new InvalidProtocolBufferException("Invalid field type for map: " + field.FieldType);
            }
        }

        private static object ParseSingleNumberValue(FieldDescriptor field, JsonToken token)
        {
            double value = token.NumberValue;
            checked
            {
                // TODO: Validate that it's actually an integer, possibly in terms of the textual representation?                
                try
                {
                    switch (field.FieldType)
                    {
                        case FieldType.Int32:
                        case FieldType.SInt32:
                        case FieldType.SFixed32:
                            return (int) value;
                        case FieldType.UInt32:
                        case FieldType.Fixed32:
                            return (uint) value;
                        case FieldType.Int64:
                        case FieldType.SInt64:
                        case FieldType.SFixed64:
                            return (long) value;
                        case FieldType.UInt64:
                        case FieldType.Fixed64:
                            return (ulong) value;
                        case FieldType.Double:
                            return value;
                        case FieldType.Float:
                            if (double.IsNaN(value))
                            {
                                return float.NaN;
                            }
                            if (value > float.MaxValue || value < float.MinValue)
                            {
                                if (double.IsPositiveInfinity(value))
                                {
                                    return float.PositiveInfinity;
                                }
                                if (double.IsNegativeInfinity(value))
                                {
                                    return float.NegativeInfinity;
                                }
                                throw new InvalidProtocolBufferException("Value out of range: " + value);
                            }
                            return (float) value;
                        default:
                            throw new InvalidProtocolBufferException("Unsupported conversion from JSON number for field type " + field.FieldType);
                    }
                }
                catch (OverflowException)
                {
                    throw new InvalidProtocolBufferException("Value out of range: " + value);
                }
            }
        }

        private static object ParseSingleStringValue(FieldDescriptor field, string text)
        {
            switch (field.FieldType)
            {
                case FieldType.String:
                    return text;
                case FieldType.Bytes:
                    return ByteString.FromBase64(text);
                case FieldType.Int32:
                case FieldType.SInt32:
                case FieldType.SFixed32:
                    return ParseNumericString(text, int.Parse, false);
                case FieldType.UInt32:
                case FieldType.Fixed32:
                    return ParseNumericString(text, uint.Parse, false);
                case FieldType.Int64:
                case FieldType.SInt64:
                case FieldType.SFixed64:
                    return ParseNumericString(text, long.Parse, false);
                case FieldType.UInt64:
                case FieldType.Fixed64:
                    return ParseNumericString(text, ulong.Parse, false);
                case FieldType.Double:
                    double d = ParseNumericString(text, double.Parse, true);
                    // double.Parse can return +/- infinity on Mono for non-infinite values which are out of range for double.
                    if (double.IsInfinity(d) && !text.Contains("Infinity"))
                    {
                        throw new InvalidProtocolBufferException("Invalid numeric value: " + text);
                    }
                    return d;
                case FieldType.Float:
                    float f = ParseNumericString(text, float.Parse, true);
                    // float.Parse can return +/- infinity on Mono for non-infinite values which are out of range for float.
                    if (float.IsInfinity(f) && !text.Contains("Infinity"))
                    {
                        throw new InvalidProtocolBufferException("Invalid numeric value: " + text);
                    }
                    return f;
                case FieldType.Enum:
                    var enumValue = field.EnumType.FindValueByName(text);
                    if (enumValue == null)
                    {
                        throw new InvalidProtocolBufferException("Invalid enum value: " + text + " for enum type: " + field.EnumType.FullName);
                    }
                    // Just return it as an int, and let the CLR convert it.
                    return enumValue.Number;
                default:
                    throw new InvalidProtocolBufferException("Unsupported conversion from JSON string for field type " + field.FieldType);
            }
        }

        /// <summary>
        /// Creates a new instance of the message type for the given field.
        /// This method is mostly extracted so we can replace it in one go when we work out
        /// what we want to do instead of Activator.CreateInstance.
        /// </summary>
        private static IMessage NewMessageForField(FieldDescriptor field)
        {
            // TODO: Create an instance in a better way ?
            // (We could potentially add a Parser property to MessageDescriptor... see issue 806.)
            return (IMessage) Activator.CreateInstance(field.MessageType.GeneratedType);
        }

        private static T ParseNumericString<T>(string text, Func<string, NumberStyles, IFormatProvider, T> parser, bool floatingPoint)
        {
            // TODO: Prohibit leading zeroes (but allow 0!)
            // TODO: Validate handling of "Infinity" etc. (Should be case sensitive, no leading whitespace etc)
            // Can't prohibit this with NumberStyles.
            if (text.StartsWith("+"))
            {
                throw new InvalidProtocolBufferException("Invalid numeric value: " + text);
            }
            if (text.StartsWith("0") && text.Length > 1)
            {
                if (text[1] >= '0' && text[1] <= '9')
                {
                    throw new InvalidProtocolBufferException("Invalid numeric value: " + text);
                }
            }
            else if (text.StartsWith("-0") && text.Length > 2)
            {
                if (text[2] >= '0' && text[2] <= '9')
                {
                    throw new InvalidProtocolBufferException("Invalid numeric value: " + text);
                }
            }
            try
            {
                var styles = floatingPoint
                    ? NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent
                    : NumberStyles.AllowLeadingSign;
                return parser(text, styles, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                throw new InvalidProtocolBufferException("Invalid numeric value for type: " + text);
            }
            catch (OverflowException)
            {
                throw new InvalidProtocolBufferException("Value out of range: " + text);
            }
        }

        private static void MergeTimestamp(IMessage message, JsonToken token)
        {
            if (token.Type != JsonToken.TokenType.StringValue)
            {
                throw new InvalidProtocolBufferException("Expected string value for Timestamp");
            }
            var match = TimestampRegex.Match(token.StringValue);
            if (!match.Success)
            {
                throw new InvalidProtocolBufferException("Invalid Timestamp value: " + token.StringValue);
            }
            var dateTime = match.Groups["datetime"].Value;
            var subseconds = match.Groups["subseconds"].Value;
            var offset = match.Groups["offset"].Value;

            try
            {
                DateTime parsed = DateTime.ParseExact(
                    dateTime,
                    "yyyy-MM-dd'T'HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                // TODO: It would be nice not to have to create all these objects... easy to optimize later though.
                Timestamp timestamp = Timestamp.FromDateTime(parsed);
                int nanosToAdd = 0;
                if (subseconds != "")
                {
                    // This should always work, as we've got 1-9 digits.
                    int parsedFraction = int.Parse(subseconds.Substring(1), CultureInfo.InvariantCulture);
                    nanosToAdd = parsedFraction * SubsecondScalingFactors[subseconds.Length];
                }
                int secondsToAdd = 0;
                if (offset != "Z")
                {
                    // This is the amount we need to *subtract* from the local time to get to UTC - hence - => +1 and vice versa.
                    int sign = offset[0] == '-' ? 1 : -1;
                    int hours = int.Parse(offset.Substring(1, 2), CultureInfo.InvariantCulture);
                    int minutes = int.Parse(offset.Substring(4, 2));
                    int totalMinutes = hours * 60 + minutes;
                    if (totalMinutes > 18 * 60)
                    {
                        throw new InvalidProtocolBufferException("Invalid Timestamp value: " + token.StringValue);
                    }
                    if (totalMinutes == 0 && sign == 1)
                    {
                        // This is an offset of -00:00, which means "unknown local offset". It makes no sense for a timestamp.
                        throw new InvalidProtocolBufferException("Invalid Timestamp value: " + token.StringValue);
                    }
                    // We need to *subtract* the offset from local time to get UTC.
                    secondsToAdd = sign * totalMinutes * 60;
                }
                // Ensure we've got the right signs. Currently unnecessary, but easy to do.
                if (secondsToAdd < 0 && nanosToAdd > 0)
                {
                    secondsToAdd++;
                    nanosToAdd = nanosToAdd - Duration.NanosecondsPerSecond;
                }
                if (secondsToAdd != 0 || nanosToAdd != 0)
                {
                    timestamp += new Duration { Nanos = nanosToAdd, Seconds = secondsToAdd };
                    // The resulting timestamp after offset change would be out of our expected range. Currently the Timestamp message doesn't validate this
                    // anywhere, but we shouldn't parse it.
                    if (timestamp.Seconds < Timestamp.UnixSecondsAtBclMinValue || timestamp.Seconds > Timestamp.UnixSecondsAtBclMaxValue)
                    {
                        throw new InvalidProtocolBufferException("Invalid Timestamp value: " + token.StringValue);
                    }
                }
                message.Descriptor.Fields[Timestamp.SecondsFieldNumber].Accessor.SetValue(message, timestamp.Seconds);
                message.Descriptor.Fields[Timestamp.NanosFieldNumber].Accessor.SetValue(message, timestamp.Nanos);
            }
            catch (FormatException)
            {
                throw new InvalidProtocolBufferException("Invalid Timestamp value: " + token.StringValue);
            }
        }

        private static void MergeDuration(IMessage message, JsonToken token)
        {
            if (token.Type != JsonToken.TokenType.StringValue)
            {
                throw new InvalidProtocolBufferException("Expected string value for Duration");
            }
            var match = DurationRegex.Match(token.StringValue);
            if (!match.Success)
            {
                throw new InvalidProtocolBufferException("Invalid Duration value: " + token.StringValue);
            }
            var sign = match.Groups["sign"].Value;
            var secondsText = match.Groups["int"].Value;
            // Prohibit leading insignficant zeroes
            if (secondsText[0] == '0' && secondsText.Length > 1)
            {
                throw new InvalidProtocolBufferException("Invalid Duration value: " + token.StringValue);
            }
            var subseconds = match.Groups["subseconds"].Value;
            var multiplier = sign == "-" ? -1 : 1;

            try
            {
                long seconds = long.Parse(secondsText, CultureInfo.InvariantCulture);
                int nanos = 0;
                if (subseconds != "")
                {
                    // This should always work, as we've got 1-9 digits.
                    int parsedFraction = int.Parse(subseconds.Substring(1));
                    nanos = parsedFraction * SubsecondScalingFactors[subseconds.Length];
                }
                if (seconds >= Duration.MaxSeconds)
                {
                    // Allow precisely 315576000000 seconds, but prohibit even 1ns more.
                    if (seconds > Duration.MaxSeconds || nanos > 0)
                    {
                        throw new InvalidProtocolBufferException("Invalid Duration value: " + token.StringValue);
                    }
                }
                message.Descriptor.Fields[Duration.SecondsFieldNumber].Accessor.SetValue(message, seconds * multiplier);
                message.Descriptor.Fields[Duration.NanosFieldNumber].Accessor.SetValue(message, nanos * multiplier);
            }
            catch (FormatException)
            {
                throw new InvalidProtocolBufferException("Invalid Duration value: " + token.StringValue);
            }
        }

        private static void MergeFieldMask(IMessage message, JsonToken token)
        {
            if (token.Type != JsonToken.TokenType.StringValue)
            {
                throw new InvalidProtocolBufferException("Expected string value for FieldMask");
            }
            // TODO: Do we *want* to remove empty entries? Probably okay to treat "" as "no paths", but "foo,,bar"?
            string[] jsonPaths = token.StringValue.Split(FieldMaskPathSeparators, StringSplitOptions.RemoveEmptyEntries);
            IList messagePaths = (IList) message.Descriptor.Fields[FieldMask.PathsFieldNumber].Accessor.GetValue(message);
            foreach (var path in jsonPaths)
            {
                messagePaths.Add(ToSnakeCase(path));
            }
        }
        
        // Ported from src/google/protobuf/util/internal/utility.cc
        private static string ToSnakeCase(string text)
        {
            var builder = new StringBuilder(text.Length * 2);
            bool wasNotUnderscore = false;  // Initialize to false for case 1 (below)
            bool wasNotCap = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 'A' && c <= 'Z') // ascii_isupper
                {
                    // Consider when the current character B is capitalized:
                    // 1) At beginning of input:   "B..." => "b..."
                    //    (e.g. "Biscuit" => "biscuit")
                    // 2) Following a lowercase:   "...aB..." => "...a_b..."
                    //    (e.g. "gBike" => "g_bike")
                    // 3) At the end of input:     "...AB" => "...ab"
                    //    (e.g. "GoogleLAB" => "google_lab")
                    // 4) Followed by a lowercase: "...ABc..." => "...a_bc..."
                    //    (e.g. "GBike" => "g_bike")
                    if (wasNotUnderscore &&               //            case 1 out
                        (wasNotCap ||                     // case 2 in, case 3 out
                         (i + 1 < text.Length &&         //            case 3 out
                          (text[i + 1] >= 'a' && text[i + 1] <= 'z')))) // ascii_islower(text[i + 1])
                    {  // case 4 in
                       // We add an underscore for case 2 and case 4.
                        builder.Append('_');
                    }
                    // ascii_tolower, but we already know that c *is* an upper case ASCII character...
                    builder.Append((char) (c + 'a' - 'A'));
                    wasNotUnderscore = true;
                    wasNotCap = false;
                }
                else
                {
                    builder.Append(c);
                    wasNotUnderscore = c != '_';
                    wasNotCap = true;
                }
            }
            return builder.ToString();
        }
        #endregion

        /// <summary>
        /// Settings controlling JSON parsing. (Currently doesn't have any actual settings, but I suspect
        /// we'll want them for levels of strictness, descriptor pools for Any handling, etc.)
        /// </summary>
        public sealed class Settings
        {
            private static readonly Settings defaultInstance = new Settings();

            // TODO: Add recursion limit.

            /// <summary>
            /// Default settings, as used by <see cref="JsonParser.Default"/>
            /// </summary>
            public static Settings Default { get { return defaultInstance; } }

            /// <summary>
            /// Creates a new <see cref="Settings"/> object.
            /// </summary>
            public Settings()
            {
            }
        }
    }
}
