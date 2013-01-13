﻿using System;
using System.Collections.Generic;
using System.IO;


namespace fNbt {
    /// <summary> Represents a reader that provides fast, noncached, forward-only access to NBT data. </summary>
    public class NbtReader {
        NbtParseState state = NbtParseState.AtStreamBeginning;
        readonly NbtBinaryReader reader;
        readonly Stack<NbtReaderNode> nodes = new Stack<NbtReaderNode>();
        readonly long streamStartOffset;
        bool atValue;
        object valueCache;


        /// <summary> Initializes a new instance of the NbtReader class. </summary>
        /// <param name="stream"> Stream to read from. </param>
        /// <param name="bigEndian"> Whether NBT data is in Big-Endian encoding. </param>
        public NbtReader( Stream stream, bool bigEndian = true ) {
            SkipEndTags = true;
            CacheTagValues = false;
            ParentTagType = NbtTagType.Unknown;
            TagType = NbtTagType.Unknown;
            streamStartOffset = stream.Position;
            reader = new NbtBinaryReader( stream, bigEndian );
        }


        /// <summary> Gets the name of the root tag of this NBT stream. </summary>
        public string RootName { get; private set; }

        /// <summary> Gets the name of the parent tag. May be null (for root tags and descendants of list elements). </summary>
        public string ParentName { get; private set; }

        /// <summary> Gets the name of the current tag. May be null (for list elements and end tags). </summary>
        public string TagName { get; private set; }


        /// <summary> Gets the type of the parent tag. Returns TagType.Unknown if there is no parent tag. </summary>
        public NbtTagType ParentTagType { get; private set; }

        /// <summary> Gets the type of the current tag. </summary>
        public NbtTagType TagType { get; private set; }


        /// <summary> Whether tag that we are currently on is a list element. </summary>
        public bool IsListElement {
            get {
                return ( state == NbtParseState.InList );
            }
        }

        /// <summary> Whether current tag has a value to read. </summary>
        public bool HasValue {
            get {
                switch( TagType ) {
                    case NbtTagType.Compound:
                    case NbtTagType.End:
                    case NbtTagType.List:
                        return false;
                    default:
                        return true;
                    case NbtTagType.Unknown:
                        ThrowNotRead();
                        return false;
                }
            }
        }

        /// <summary> Whether current tag has a name. </summary>
        public bool HasName {
            get {
                return ( TagName != null );
            }
        }


        /// <summary> Whether the current tag is TAG_Compound. </summary>
        public bool IsCompound {
            get {
                return ( TagType == NbtTagType.Compound );
            }
        }

        /// <summary> Whether the current tag is TAG_List. </summary>
        public bool IsList {
            get {
                return ( TagType == NbtTagType.List );
            }
        }

        /// <summary> Whether the current tag is TAG_List. </summary>
        public bool HasLength {
            get {
                return ( TagType == NbtTagType.List || TagType == NbtTagType.ByteArray || TagType == NbtTagType.IntArray );
            }
        }

        /// <summary> Whether the current tag is TAG_End. </summary>
        public bool IsEnd {
            get {
                return ( TagType == NbtTagType.End );
            }
        }


        /// <summary> Stream from which data is being read. </summary>
        public Stream BaseStream {
            get {
                return reader.BaseStream;
            }
        }

        /// <summary> Gets the number of bytes from the beginning of the stream to the beginning of this tag. </summary>
        public int TagStartOffset { get; private set; }

        /// <summary> Gets the number of tags read from the stream so far
        /// (including the current tag, all end tags, and all skipped tags). </summary>
        public int TagsRead { get; private set; }


        /// <summary> Gets the depth of the current tag in the hierarchy. RootTag is 0, its descendant tags are 1, etc. </summary>
        public int Depth { get; private set; }


        /// <summary> If the current tag is TAG_List, returns type of the list elements. </summary>
        public NbtTagType ListType { get; private set; }

        /// <summary> If the current tag is TAG_List, TAG_Byte_Array, or TAG_Int_Array, returns the number of elements. </summary>
        public int TagLength { get; private set; }

        /// <summary> If the parent tag is TAG_List, returns the number of elements. </summary>
        public int ParentTagLength { get; private set; }

        /// <summary> If the current tag is TAG_List, returns index of the current elements. </summary>
        public int ListIndex { get; private set; }


        /// <summary> Reads the next tag from the stream. </summary>
        /// <returns> true if the next tag was read successfully; false if there are no more tags to read. </returns>
        public bool ReadToFollowing() {
            switch( state ) {
                case NbtParseState.AtStreamBeginning:
                    // read first tag, make sure it's a compound
                    if( reader.ReadTagType() != NbtTagType.Compound ) {
                        state = NbtParseState.Error;
                        throw new NbtFormatException( "Given NBT stream does not start with a TAG_Compound" );
                    }
                    TagType = NbtTagType.Compound;
                    // Read root name. Advance to the first inside tag.
                    ReadTagHeader( true );
                    RootName = TagName;
                    return true;

                case NbtParseState.AtCompoundBeginning:
                    GoDown();
                    state = NbtParseState.InCompound;
                    goto case NbtParseState.InCompound;

                case NbtParseState.InCompound:
                    if( atValue )
                        SkipValue();
                    // Read next tag, check if we've hit the end
                    TagStartOffset = (int)( reader.BaseStream.Position - streamStartOffset );
                    TagType = reader.ReadTagType();
                    if( TagType == NbtTagType.End ) {
                        TagName = null;
                        TagsRead++;
                        state = NbtParseState.AtCompoundEnd;
                        if( SkipEndTags ) {
                            goto case NbtParseState.AtCompoundEnd;
                        } else {
                            return true;
                        }
                    } else {
                        ReadTagHeader( true );
                        return true;
                    }

                case NbtParseState.AtListBeginning:
                    GoDown();
                    ListIndex = -1;
                    TagType = ListType;
                    state = NbtParseState.InList;
                    goto case NbtParseState.InList;

                case NbtParseState.InList:
                    if( atValue )
                        SkipValue();
                    ListIndex++;
                    if( ListIndex >= ParentTagLength ) {
                        GoUp();
                        if( ParentTagType == NbtTagType.List ) {
                            state = NbtParseState.InList;
                            TagType = ListType;
                            goto case NbtParseState.InList;
                        } else if( ParentTagType == NbtTagType.Compound ) {
                            state = NbtParseState.InCompound;
                            goto case NbtParseState.InCompound;
                        } else {
                            // This should not happen unless NbtReader is bugged
                            state = NbtParseState.Error;
                            throw new NbtFormatException( "Tag parent is neither a List nor a Compound!" );
                        }
                    } else {
                        TagStartOffset = (int)( reader.BaseStream.Position - streamStartOffset );
                        ReadTagHeader( false );
                    }
                    return true;

                case NbtParseState.AtCompoundEnd:
                    GoUp();
                    if( ParentTagType == NbtTagType.List ) {
                        state = NbtParseState.InList;
                        TagType = ListType;
                        goto case NbtParseState.InList;
                    } else if( ParentTagType == NbtTagType.Compound ) {
                        state = NbtParseState.InCompound;
                        goto case NbtParseState.InCompound;
                    } else if( ParentTagType == NbtTagType.Unknown ) {
                        state = NbtParseState.AtStreamEnd;
                        return false;
                    } else {
                        // This should not happen unless NbtReader is bugged
                        state = NbtParseState.Error;
                        throw new NbtFormatException( "Tag parent is neither a List nor a Compound!" );
                    }

                case NbtParseState.AtStreamEnd:
                    // nothing left to read!
                    return false;

                case NbtParseState.Error:
                    // previous call produced a parsing error
                    throw new InvalidOperationException( ErroneousStateMessage );
            }
            return true;
        }


        const string ErroneousStateMessage = "NbtReader is in an erroneous state!";

        void ReadTagHeader( bool readName ) {
            TagsRead++;
            TagName = (readName ? reader.ReadString() : null);

            valueCache = null;
            TagLength = 0;
            atValue = false;
            ListType = NbtTagType.Unknown;
            
            switch( TagType ) {
                case NbtTagType.Byte:
                case NbtTagType.Short:
                case NbtTagType.Int:
                case NbtTagType.Long:
                case NbtTagType.Float:
                case NbtTagType.Double:
                case NbtTagType.String:
                    atValue = true;
                    break;

                case NbtTagType.IntArray:
                case NbtTagType.ByteArray:
                    TagLength = reader.ReadInt32();
                    atValue = true;
                    break;

                case NbtTagType.List:
                    ListType = reader.ReadTagType();
                    TagLength = reader.ReadInt32();
                    state = NbtParseState.AtListBeginning;
                    break;

                case NbtTagType.Compound:
                    state = NbtParseState.AtCompoundBeginning;
                    break;

                default:
                    throw new NbtFormatException( "Trying to read tag of unknown type." );
            }
        }


        // Goes one step down the NBT file's hierarchy, preserving current state
        void GoDown() {
            NbtReaderNode newNode = new NbtReaderNode {
                ListIndex = ListIndex,
                ParentTagLength = ParentTagLength,
                ParentName = ParentName,
                ParentTagType = ParentTagType,
                ListType = ListType
            };
            nodes.Push( newNode );

            ParentName = TagName;
            ParentTagType = TagType;
            ParentTagLength = TagLength;
            ListIndex = 0;
            TagLength = 0;

            Depth++;
        }


        // Goes one step up the NBT file's hierarchy, restoring previous state
        void GoUp() {
            NbtReaderNode oldNode = nodes.Pop();

            ParentName = oldNode.ParentName;
            ParentTagType = oldNode.ParentTagType;
            ListIndex = oldNode.ListIndex;
            ListType = oldNode.ListType;
            ParentTagLength = oldNode.ParentTagLength;
            TagLength = 0;

            Depth--;
        }


        void SkipValue() {
            if( !atValue ) throw new InvalidOperationException();
            switch( TagType ) {
                case NbtTagType.Byte:
                    reader.ReadByte();
                    break;

                case NbtTagType.Short:
                    reader.ReadInt16();
                    break;

                case NbtTagType.Float:
                case NbtTagType.Int:
                    reader.ReadInt32();
                    break;

                case NbtTagType.Double:
                case NbtTagType.Long:
                    reader.ReadInt64();
                    break;

                case NbtTagType.ByteArray:
                    reader.Skip( TagLength );
                    break;

                case NbtTagType.IntArray:
                    reader.Skip( sizeof( int ) * TagLength );
                    break;

                case NbtTagType.String:
                    reader.SkipString();
                    break;

                default:
                    throw new InvalidOperationException( "Trying to skip value of a non-value tag." );
            }
            atValue = false;
            valueCache = null;
        }


        /// <summary> Reads until a tag with the specified name is found. 
        /// Returns false if the end of stream is reached. </summary>
        /// <param name="tagName"> Name of the tag. </param>
        /// <returns> <c>true</c> if a matching tag is found; otherwise <c>false</c>. </returns>
        public bool ReadToFollowing( string tagName ) {
            while( ReadToFollowing() ) {
                if( TagName.Equals( tagName ) ) {
                    return true;
                }
            }
            return false;
        }


        /// <summary> Advances the NbtReader to the next descendant tag with the specified name.
        /// If a matching child tag is not found, the NbtReader is positioned on the end tag. </summary>
        /// <param name="tagName"> Name of the tag you wish to move to. </param>
        /// <returns> true if a matching descendant tag is found; otherwise false. </returns>
        public bool ReadToDescendant( string tagName ) {
            throw new NotImplementedException();
        }


        /// <summary> Advances the NbtReader to the next sibling tag with the specified name.
        /// If a matching sibling tag is not found, the NbtReader is positioned on the end tag of the parent tag. </summary>
        /// <param name="tagName"> The name of the sibling tag you wish to move to. </param>
        /// <returns> true if a matching sibling element is found; otherwise false. </returns>
        public bool ReadToNextSibling( string tagName ) {
            throw new NotImplementedException();
        }


        /// <summary> Skips current tag, its value/descendants, and any following siblings.
        /// In other words, reads until parent tag's subling. </summary>
        /// <returns> Total number of tags that were skipped. </returns>
        /// <exception cref="InvalidOperationException"> If parser is in erroneous state. </exception>
        public int SkipSiblings() {
            if( state == NbtParseState.Error ) {
                throw new InvalidOperationException( ErroneousStateMessage );
            } else if( state == NbtParseState.AtStreamEnd ) {
                return 0;
            }
            int startDepth = Depth;
            int skipped = 0;
            while( ReadToFollowing() && Depth >= startDepth ) {
                skipped++;
            }
            return skipped;
        }


        /// <summary> Reads the entirety of the current tag, including any descendants,
        /// and constructs an NbtTag object of the appropriate type. </summary>
        /// <returns> Constructed NbtTag object;
        /// <c>null</c> if <c>SkipEndTags</c> is <c>true</c> and trying to read an End tag. </returns>
        public NbtTag ReadAsTag() {
            if( atValue ) {
                return ReadValueAsTag();
            }
            switch( state ) {
                case NbtParseState.AtStreamBeginning:
                    // read whole file as tag
                case NbtParseState.AtCompoundBeginning:
                    // read whole compound
                    break;

                case NbtParseState.InCompound:
                    // read tag within a compound
                    break;

                case NbtParseState.AtListBeginning:
                    // read whole list
                    break;

                case NbtParseState.InList:
                    // read list item
                    break;

                case NbtParseState.AtCompoundEnd:
                    // end tag.
                    return null;

                case NbtParseState.AtStreamEnd:
                    throw new InvalidOperationException( "End of stream: no more NBT data to read." );

                case NbtParseState.Error:
                    // previous call produced a parsing error
                    throw new InvalidOperationException( ErroneousStateMessage );
            }
            return null;
        }


        NbtTag ReadValueAsTag() {
            switch( TagType ) {
                case NbtTagType.Byte:
                    return new NbtByte( TagName, reader.ReadByte() );

                case NbtTagType.Short:
                    return new NbtShort( TagName, reader.ReadInt16() );

                case NbtTagType.Int:
                    return new NbtInt( TagName, reader.ReadInt32() );

                case NbtTagType.Long:
                    return new NbtLong( TagName, reader.ReadInt64() );

                case NbtTagType.Float:
                    return new NbtFloat( TagName, reader.ReadSingle() );

                case NbtTagType.Double:
                    return new NbtDouble( TagName, reader.ReadDouble() );

                case NbtTagType.String:
                    return new NbtString( TagName, reader.ReadString() );

                case NbtTagType.ByteArray:
                    return new NbtByteArray( TagName, reader.ReadBytes( TagLength ) );

                case NbtTagType.IntArray:
                    int[] ints = new int[TagLength];
                    for( int i = 0; i < TagLength; i++ ) {
                        ints[i] = reader.ReadInt32();
                    }
                    return new NbtIntArray( TagName, ints );

                default:
                    throw new InvalidOperationException();
            }
        }


        /// <summary> Reads the value as an object of the type specified. </summary>
        /// <typeparam name="T"> The type of the value to be returned.
        /// Tag value should be convertible to this type. </typeparam>
        /// <returns> Tag value converted to the requested type. </returns>
        public T ReadValueAs<T>() {
            return (T)ReadValue();
        }


        /// <summary> Reads the value as an object of the correct type, boxed.
        /// Cannot be called for tags that do not have a single-object value (compound, list, and end tags). </summary>
        /// <returns> Tag value converted to the requested type. </returns>
        public object ReadValue() {
            if( !atValue ) {
                if( cacheTagValues ) {
                    if( valueCache == null ) {
                        throw new InvalidOperationException( "No value to read." );
                    } else {
                        return valueCache;
                    }
                } else {
                    throw new InvalidOperationException( NoValueToReadError );
                }
            }
            valueCache = null;
            atValue = false;
            object value;
            switch( TagType ) {
                case NbtTagType.Byte:
                    value = reader.ReadByte();
                    break;

                case NbtTagType.Short:
                    value = reader.ReadInt16();
                    break;

                case NbtTagType.Float:
                    value = reader.ReadSingle();
                    break;

                case NbtTagType.Int:
                    value = reader.ReadInt32();
                    break;

                case NbtTagType.Double:
                    value = reader.ReadDouble();
                    break;

                case NbtTagType.Long:
                    value = reader.ReadInt64();
                    break;

                case NbtTagType.ByteArray:
                    value = reader.ReadBytes( TagLength );
                    break;

                case NbtTagType.IntArray:
                    int[] intValue = new int[TagLength];
                    for( int i = 0; i < TagLength; i++ ) {
                        intValue[i] = reader.ReadInt32();
                    }
                    value = intValue;
                    break;

                case NbtTagType.String:
                    value = reader.ReadString();
                    break;

                default:
                    throw new InvalidOperationException( "Trying to read value of a non-value tag." );
            }
            if( cacheTagValues ) {
                valueCache = value;
            } else {
                valueCache = null;
            }
            return value;
        }


        const string NoValueToReadError = "Value aready read, or no value to read.";

        /// <summary> If the current tag is a TAG_List, reads all elements of this list as an array.
        /// If any tags/values have already been read from this list, only reads the remaining unread tags/values.
        /// ListType must be a value type (byte, short, int, long, float, double, or string).
        /// Stops reading after the last list element. </summary>
        /// <typeparam name="T"> Element type of the array to be returned.
        /// Tag contents should be convertible to this type. </typeparam>
        /// <returns> List contents converted to an array of the requested type. </returns>
        public T[] ReadListAsArray<T>() {
            if( TagType != NbtTagType.List ) {
                throw new InvalidOperationException( "ReadListAsArray may only be used on TAG_List tags." );
            }

            int elementsToRead = TagLength - ListIndex;

            if( ListType == NbtTagType.Byte && typeof( T ) == typeof( byte ) ) {
                TagsRead += TagLength;
                ListIndex = TagLength - 1;
                return (T[])(object)reader.ReadBytes( elementsToRead );
            }

            T[] result = new T[elementsToRead];
            switch( ListType ) {
                case NbtTagType.Byte:
                    for( int i = 0; i < elementsToRead; i++ ) {
                        result[i] = (T)Convert.ChangeType( reader.ReadByte(), typeof( T ) );
                    }
                    break;

                case NbtTagType.Short:
                    for( int i = 0; i < elementsToRead; i++ ) {
                        result[i] = (T)Convert.ChangeType( reader.ReadInt16(), typeof( T ) );
                    }
                    break;

                case NbtTagType.Int:
                    for( int i = 0; i < elementsToRead; i++ ) {
                        result[i] = (T)Convert.ChangeType( reader.ReadInt32(), typeof( T ) );
                    }
                    break;

                case NbtTagType.Long:
                    for( int i = 0; i < elementsToRead; i++ ) {
                        result[i] = (T)Convert.ChangeType( reader.ReadInt64(), typeof( T ) );
                    }
                    break;

                case NbtTagType.Float:
                    for( int i = 0; i < elementsToRead; i++ ) {
                        result[i] = (T)Convert.ChangeType( reader.ReadSingle(), typeof( T ) );
                    }
                    break;

                case NbtTagType.Double:
                    for( int i = 0; i < elementsToRead; i++ ) {
                        result[i] = (T)Convert.ChangeType( reader.ReadDouble(), typeof( T ) );
                    }
                    break;

                case NbtTagType.String:
                    for( int i = 0; i < elementsToRead; i++ ) {
                        result[i] = (T)Convert.ChangeType( reader.ReadString(), typeof( T ) );
                    }
                    break;

                default:
                    throw new InvalidOperationException( "ReadListAsArray may only be used on lists of value types." );
            }
            TagsRead += elementsToRead;
            ListIndex = TagLength - 1;
            return result;
        }


        static void ThrowNotRead() {
            throw new InvalidOperationException( "No data has been read yet!" );
        }


        /// <summary> Parsing option: Whether NbtReader should skip End tags in ReadToFollowing() automatically while parsing.
        /// Default is false. </summary>
        public bool SkipEndTags { get; set; }


        /// <summary> Parsing option: Whether NbtReader should save a copy of the most recently read tag's value.
        /// If CacheTagValues=false, tag values can only be read once. Default is false. </summary>
        public bool CacheTagValues {
            get {
                return cacheTagValues;
            }
            set {
                cacheTagValues = value;
                if( !cacheTagValues ) {
                    valueCache = null;
                }
            }
        }

        bool cacheTagValues;
    }
}