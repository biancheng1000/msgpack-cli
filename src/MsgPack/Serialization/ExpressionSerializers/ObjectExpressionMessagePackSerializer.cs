#region -- License Terms --
//
// MessagePack for CLI
//
// Copyright (C) 2010 FUJIWARA, Yusuke
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//
#endregion -- License Terms --

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.IO;
using System.Globalization;

namespace MsgPack.Serialization.ExpressionSerializers
{
	/// <summary>
	///		Implements expression tree based serializer for general object.
	/// </summary>
	/// <typeparam name="T">The type of target object.</typeparam>
	internal abstract class ObjectExpressionMessagePackSerializer<T> : MessagePackSerializer<T>, IExpressionMessagePackSerializer
	{
		private readonly Func<T, object>[] _memberGetters;

		protected Func<T, object>[] MemberGetters
		{
			get { return this._memberGetters; }
		}

		private readonly Action<T, object>[] _memberSetters;

		private readonly IMessagePackSerializer[] _memberSerializers;

		protected IMessagePackSerializer[] MemberSerializers
		{
			get { return this._memberSerializers; }
		}

		private readonly NilImplication[] _nilImplications;
		private readonly bool[] _isCollection;
		private readonly string[] _memberNames;

		protected string[] MemberNames
		{
			get { return this._memberNames; }
		}

		private readonly Dictionary<string, int> _indexMap;

		private readonly Func<T> _createInstance;

		protected ObjectExpressionMessagePackSerializer( SerializationContext context, SerializingMember[] members )
		{
			this._createInstance = Expression.Lambda<Func<T>>( Expression.New( typeof( T ).GetConstructor( Type.EmptyTypes ) ) ).Compile();
			this._memberSerializers = members.Select( m => context.GetSerializer( m.Member.GetMemberValueType() ) ).ToArray();
			this._indexMap =
				members
				.Zip( Enumerable.Range( 0, members.Length ), ( m, i ) => new KeyValuePair<MemberInfo, int>( m.Member, i ) )
				.ToDictionary( kv => kv.Key.Name, kv => kv.Value );

			var targetParameter = Expression.Parameter( typeof( T ), "target" );
			this._isCollection = members.Select( m => m.Member.GetMemberValueType().GetCollectionTraits() ).Select( t => t.CollectionType != CollectionKind.NotCollection ).ToArray();
			this._nilImplications = members.Select( m => m.Contract.NilImplication ).ToArray();
			this._memberNames = members.Select( m => m.Contract.Name ).ToArray();
			this._memberGetters =
				members.Select(
					m =>
						Expression.Lambda<Func<T, object>>(
							Expression.PropertyOrField(
								targetParameter,
								m.Member.Name
							),
							targetParameter
						).Compile()
				).ToArray();
			var valueParameter = Expression.Parameter( typeof( object ), "value" );
			this._memberSetters =
				members.Select(
					m =>
						Expression.Lambda<Action<T, object>>(
							Expression.Assign(
								Expression.PropertyOrField(
									targetParameter,
									m.Member.Name
								),
								Expression.Convert( valueParameter, m.Member.GetMemberValueType() )
							),
							targetParameter,
							valueParameter
						).Compile()
				).ToArray();
		}

		protected internal override T UnpackFromCore( Unpacker unpacker )
		{
			if ( unpacker.ItemsCount != this._memberSerializers.Length )
			{
				throw SerializationExceptions.NewUnexpectedArrayLength( this._memberSerializers.Length, unchecked( ( int )unpacker.ItemsCount ) );
			}

			// Assume subtree unpacker
			var instance = this._createInstance();
			if ( unpacker.IsArrayHeader )
			{
				this.UnpackFromArray( unpacker, instance );
			}
			else
			{
				this.UnpackFromMap( unpacker, instance );
			}

			return instance;
		}

		private void UnpackFromArray( Unpacker unpacker, T instance )
		{
			for ( int i = 0; i < this.MemberSerializers.Length; i++ )
			{
				if ( !unpacker.Read() )
				{
					throw SerializationExceptions.NewUnexpectedEndOfStream();
				}

				if ( unpacker.Data.Value.IsNil )
				{
					switch ( this._nilImplications[ i ] )
					{
						case NilImplication.Null:
						{
							this._memberSetters[ i ]( instance, null );
							break;
						}
						case NilImplication.MemberDefault:
						{
							break;
						}
						case NilImplication.Prohibit:
						{
							throw SerializationExceptions.NewNullIsProhibited( this._memberNames[ i ] );
						}
					}

					continue;
				}

				this._memberSetters[ i ]( instance, this.MemberSerializers[ i ].UnpackFrom( unpacker ) );
			}
		}

		private void UnpackFromMap( Unpacker unpacker, T instance )
		{
			while ( unpacker.Read() )
			{
				var memberName = unpacker.Data.Value.AsString();
				int index;
				if ( !this._indexMap.TryGetValue( memberName, out index ) )
				{
					// TODO: unknown member handling.
					continue;
				}

				if ( unpacker.Data.Value.IsNil )
				{
					switch ( this._nilImplications[ index ] )
					{
						case NilImplication.Null:
						{
							this._memberSetters[ index ]( instance, null );
							continue;
						}
						case NilImplication.MemberDefault:
						{
							continue;
						}
						case NilImplication.Prohibit:
						{
							throw SerializationExceptions.NewNullIsProhibited( this._memberNames[ index ] );
						}
					}
				}

				this._memberSetters[ index ]( instance, this.MemberSerializers[ index ].UnpackFrom( unpacker ) );
			}
		}

		public override string ToString()
		{
			var buffer = new StringBuilder( Int16.MaxValue );
			using ( var writer = new StringWriter( buffer ) )
			{
				this.ToStringCore( writer, 0 );
			}

			return buffer.ToString();
		}

		void IExpressionMessagePackSerializer.ToString( TextWriter writer, int depth )
		{
			this.ToStringCore( writer ?? TextWriter.Null, depth < 0 ? 0 : depth );
		}

		private void ToStringCore( TextWriter writer, int depth )
		{
			var name = this.GetType().Name;
			int indexOfAgusam = name.IndexOf( '`' );
			int nameLength = indexOfAgusam < 0 ? name.Length : indexOfAgusam;
			for ( int i = 0; i < nameLength; i++ )
			{
				writer.Write( name[ i ] );
			}

			writer.Write( "For" );
			writer.WriteLine( typeof( T ) );

			for ( int i = 0; i < this._memberSerializers.Length; i++ )
			{
				ExpressionDumper.WriteIndent( writer, depth + 1 );
				writer.Write( this._memberNames[ i ] );
				writer.Write( " : " );
				var expressionSerializer = this._memberSerializers[ i ] as IExpressionMessagePackSerializer;
				if ( expressionSerializer != null )
				{
					expressionSerializer.ToString( writer, depth + 2 );
				}
				else
				{
					writer.Write( this._memberSerializers[ i ] );
				}

				writer.WriteLine();
			}
		}
	}
}