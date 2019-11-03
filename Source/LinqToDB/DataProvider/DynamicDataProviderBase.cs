﻿using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;

namespace LinqToDB.DataProvider
{
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using Configuration;
	using Extensions;
	using Mapping;

	public abstract class DynamicDataProviderBase : DataProviderBase
	{
		protected DynamicDataProviderBase(string name, MappingSchema mappingSchema)
			: base(name, mappingSchema)
		{
		}

		protected abstract string ConnectionTypeName { get; }
		protected abstract string DataReaderTypeName { get; }

		protected static readonly object SyncRoot = new object();

		protected abstract void OnConnectionTypeCreated(Type connectionType);

		protected void EnsureConnection()
		{
			GetConnectionType();
		}

		volatile Type? _connectionType;

		public override bool IsCompatibleConnection(IDbConnection connection)
		{
			return GetConnectionType().IsSameOrParentOf(Proxy.GetUnderlyingObject((DbConnection)connection).GetType());
		}

		private Type? _dataReaderType;

		// DbProviderFactories supported added to netcoreapp2.1/netstandard2.1, but we don't build those targets yet
#if NETSTANDARD2_0
		public override Type DataReaderType => _dataReaderType ?? (_dataReaderType = Type.GetType(DataReaderTypeName, true));

		protected internal virtual Type GetConnectionType()
		{
			if (_connectionType == null)
				lock (SyncRoot)
					if (_connectionType == null)
					{
						var connectionType = Type.GetType(ConnectionTypeName, true);

						OnConnectionTypeCreated(connectionType);

						_connectionType = connectionType;
					}

			return _connectionType;
		}
#else
		public virtual string? DbFactoryProviderName => null;

		public override Type DataReaderType
		{
			get
			{
				if (_dataReaderType != null)
					return _dataReaderType;

				if (DbFactoryProviderName == null)
					return _dataReaderType = Type.GetType(DataReaderTypeName, true);

				_dataReaderType = Type.GetType(DataReaderTypeName, false);

				if (_dataReaderType == null)
				{
					var assembly = DbProviderFactories.GetFactory(DbFactoryProviderName).GetType().Assembly;

					int idx;
					var dataReaderTypeName = (idx = DataReaderTypeName.IndexOf(',')) != -1 ? DataReaderTypeName.Substring(0, idx) : DataReaderTypeName;
					_dataReaderType = assembly.GetType(dataReaderTypeName, true);
				}

				return _dataReaderType;
			}
		}

		protected internal virtual Type GetConnectionType()
		{
			if (_connectionType == null)
				lock (SyncRoot)
					if (_connectionType == null)
					{
						Type connectionType;

						if (DbFactoryProviderName == null)
							connectionType = Type.GetType(ConnectionTypeName, true);
						else
						{
							connectionType = Type.GetType(ConnectionTypeName, false);

							if (connectionType == null)
								using (var db = DbProviderFactories.GetFactory(DbFactoryProviderName).CreateConnection())
									connectionType = db.GetType();
						}

						OnConnectionTypeCreated(connectionType);

						_connectionType = connectionType;
					}

			return _connectionType;
		}
#endif

		Func<string, IDbConnection>? _createConnection;

		protected override IDbConnection CreateConnectionInternal(string connectionString)
		{
			if (_createConnection == null)
			{
				var l = CreateConnectionExpression(GetConnectionType());
				_createConnection = l.Compile();
			}

			return _createConnection(connectionString);
		}

		public static Expression<Func<string, IDbConnection>> CreateConnectionExpression(Type connectionType)
		{
			var p = Expression.Parameter(typeof(string));
			var l = Expression.Lambda<Func<string, IDbConnection>>(
				Expression.New(connectionType.GetConstructor(new[] { typeof(string) }), p),
				p);

			return l;
		}

		#region Expression Helpers

		protected Action<IDbDataParameter, TResult> GetSetParameter<TResult>(
			Type connectionType,
			string parameterTypeName, string propertyName, Type dbType)
		{
			var pType = connectionType.Assembly.GetType(parameterTypeName.Contains(".") ? parameterTypeName : connectionType.Namespace + "." + parameterTypeName, true);
			return GetSetParameter<TResult>(pType, propertyName, dbType);
		}

		protected Action<IDbDataParameter, TResult> GetSetParameter<TResult>(
			Type parameterType, string propertyName, Type dbType)
		{
			var p = Expression.Parameter(typeof(IDbDataParameter));
			var v = Expression.Parameter(typeof(TResult));
			var l = Expression.Lambda<Action<IDbDataParameter, TResult>>(
				Expression.Assign(
					Expression.PropertyOrField(
						Expression.Convert(p, parameterType),
						propertyName),
					Expression.Convert(v, dbType)),
				p, v);

			return l.Compile();
		}

		protected Func<IDbDataParameter, TResult> GetGetParameter<TResult>(Type parameterType, string propertyName)
		{
			var p = Expression.Parameter(typeof(IDbDataParameter));
			var l = Expression.Lambda<Func<IDbDataParameter, TResult>>(
					Expression.PropertyOrField(
						Expression.Convert(p, parameterType),
						propertyName),
				p);

			return l.Compile();
		}

		protected Action<IDbDataParameter> GetSetParameter(
			Type connectionType,
			string parameterTypeName, string propertyName, Type dbType, string valueName)
		{
			var pType = connectionType.Assembly.GetType(parameterTypeName.Contains(".") ? parameterTypeName : connectionType.Namespace + "." + parameterTypeName, true);
			var value = Enum.Parse(dbType, valueName);

			var p = Expression.Parameter(typeof(IDbDataParameter));
			var l = Expression.Lambda<Action<IDbDataParameter>>(
				Expression.Assign(
					Expression.PropertyOrField(
						Expression.Convert(p, pType),
						propertyName),
					Expression.Constant(value)),
				p);

			return l.Compile();
		}

		protected Action<IDbDataParameter> GetSetParameter(
			Type connectionType,
			string parameterTypeName, string propertyName, string dbTypeName, string valueName)
		{
			var dbType = connectionType.Assembly.GetType(dbTypeName.Contains(".") ? dbTypeName : connectionType.Namespace + "." + dbTypeName, true);
			return GetSetParameter(connectionType, parameterTypeName, propertyName, dbType, valueName);
		}

		protected Func<IDbDataParameter, bool> IsGetParameter(
			Type connectionType,
			//   ((FbParameter)parameter).   FbDbType =           FbDbType.          TimeStamp;
			string parameterTypeName, string propertyName, string dbTypeName, string valueName)
		{
			var pType = connectionType.Assembly.GetType(parameterTypeName.Contains(".") ? parameterTypeName : connectionType.Namespace + "." + parameterTypeName, true);
			var dbType = connectionType.Assembly.GetType(dbTypeName.Contains(".") ? dbTypeName : connectionType.Namespace + "." + dbTypeName, true);
			var value = Enum.Parse(dbType, valueName);

			var p = Expression.Parameter(typeof(IDbDataParameter));
			var l = Expression.Lambda<Func<IDbDataParameter, bool>>(
				Expression.Equal(
					Expression.PropertyOrField(
						Expression.Convert(p, pType),
						propertyName),
					Expression.Constant(value)),
				p);

			return l.Compile();
		}

		// SetField<IfxDataReader,Int64>("BIGINT", (r,i) => r.GetBigInt(i));
		//
		// protected void SetField<TP,T>(string dataTypeName, Expression<Func<TP,int,T>> expr)
		// {
		//     ReaderExpressions[new ReaderInfo { FieldType = typeof(T), DataTypeName = dataTypeName }] = expr;
		// }
		protected bool SetField(Type fieldType, string dataTypeName, string methodName, bool throwException = true)
		{
			var dataReaderParameter = Expression.Parameter(DataReaderType, "r");
			var indexParameter = Expression.Parameter(typeof(int), "i");

			MethodCallExpression call;

			if (throwException)
			{
				call = Expression.Call(dataReaderParameter, methodName, null, indexParameter);
			}
			else
			{
				var methodInfo = DataReaderType.GetMethods().FirstOrDefault(m => m.Name == methodName);

				if (methodInfo == null)
					return false;

				call = Expression.Call(dataReaderParameter, methodInfo, indexParameter);
			}

			ReaderExpressions[new ReaderInfo { FieldType = fieldType, DataTypeName = dataTypeName }] =
				Expression.Lambda(
					call,
					dataReaderParameter,
					indexParameter);

			return true;
		}

		protected void SetProviderField<TField>(string methodName)
		{
			SetProviderField(typeof(TField), methodName);
		}

		protected void SetProviderField(Type fieldType, string methodName)
		{
			var dataReaderParameter = Expression.Parameter(DataReaderType, "r");
			var indexParameter = Expression.Parameter(typeof(int), "i");

			ReaderExpressions[new ReaderInfo { ProviderFieldType = fieldType }] =
				Expression.Lambda(
					Expression.Call(dataReaderParameter, methodName, null, indexParameter),
					dataReaderParameter,
					indexParameter);
		}

		// SetToTypeField<MySqlDataReader,MySqlDecimal> ((r,i) => r.GetMySqlDecimal (i));
		//
		// protected void SetToTypeField<TP,T>(Expression<Func<TP,int,T>> expr)
		// {
		//     ReaderExpressions[new ReaderInfo { ToType = typeof(T) }] = expr;
		// }
		protected void SetToTypeField(Type toType, string methodName)
		{
			var dataReaderParameter = Expression.Parameter(DataReaderType, "r");
			var indexParameter = Expression.Parameter(typeof(int), "i");

			ReaderExpressions[new ReaderInfo { ToType = toType }] =
				Expression.Lambda(
					Expression.Call(dataReaderParameter, methodName, null, indexParameter),
					dataReaderParameter,
					indexParameter);
		}

		// SetProviderField<OracleDataReader,OracleBFile,OracleBFile>((r,i) => r.GetOracleBFile(i));
		//
		// protected void SetProviderField<TP,T,TS>(Expression<Func<TP,int,T>> expr)
		// {
		//     ReaderExpressions[new ReaderInfo { ToType = typeof(T), ProviderFieldType = typeof(TS) }] = expr;
		// }

		protected bool SetProviderField<TTo, TField>(string methodName, bool throwException = true)
		{
			return SetProviderField(typeof(TTo), typeof(TField), methodName, throwException);
		}

		protected bool SetProviderField(Type toType, Type fieldType, string methodName, bool throwException = true)
		{
			var dataReaderParameter = Expression.Parameter(DataReaderType, "r");
			var indexParameter = Expression.Parameter(typeof(int), "i");

			Expression methodCall;

			if (throwException)
			{
				methodCall = Expression.Call(dataReaderParameter, methodName, null, indexParameter);
			}
			else
			{
				var methodInfo = DataReaderType.GetMethods().FirstOrDefault(m => m.Name == methodName);

				if (methodInfo == null)
					return false;

				methodCall = Expression.Call(dataReaderParameter, methodInfo, indexParameter);
			}

			if (methodCall.Type != toType)
				methodCall = Expression.Convert(methodCall, toType);

			ReaderExpressions[new ReaderInfo { ToType = toType, ProviderFieldType = fieldType }] =
				Expression.Lambda(methodCall, dataReaderParameter, indexParameter);

			return true;
		}

		#endregion

		// that's fine, as TryGetValue and indexer are lock-free operations for ConcurrentDictionary
		// in general I don't expect more than one wrapper used (e.g. miniprofiler), still it's not a big deal
		// to support multiple wrappers
		private readonly IDictionary<Type, Func<IDbDataParameter, IDbDataParameter>?> _parameterConverters  = new ConcurrentDictionary<Type, Func<IDbDataParameter, IDbDataParameter>?>();
		private readonly IDictionary<Type, Func<IDbConnection, IDbConnection>?>       _connectionConverters = new ConcurrentDictionary<Type, Func<IDbConnection, IDbConnection>?>();

		internal virtual IDbDataParameter? TryConvertParameter(Type? expectedType, IDbDataParameter parameter)
		{
			var param = parameter;
			var parameterType = parameter.GetType();

			if (expectedType != null && expectedType == parameterType)
				return parameter;

			if (!_parameterConverters.TryGetValue(parameterType, out var converter))
			{
				var converterExpr = MappingSchema.TryGetConvertExpression(parameterType, typeof(IDbDataParameter));
				if (converterExpr != null)
				{
					// TODO: does it makes sense to lock on create here?
					converter                           = (Func<IDbDataParameter, IDbDataParameter>)converterExpr.Compile();
					_parameterConverters[parameterType] = converter;
				}
			}

			if (converter != null)
				return converter(parameter);

			return null;
		}

		internal virtual IDbConnection? TryConvertConnection(Type? expectedType, IDbConnection connection)
		{
			var conn = connection;
			var connType = connection.GetType();

			if (expectedType != null && expectedType == connType)
				return connection;

			if (!_connectionConverters.TryGetValue(connType, out var converter))
			{
				var converterExpr = MappingSchema.TryGetConvertExpression(connType, typeof(IDbDataParameter));
				if (converterExpr != null)
				{
					converter                       = (Func<IDbConnection, IDbConnection>)converterExpr.Compile();
					_connectionConverters[connType] = converter;
				}
			}

			if (converter != null)
				return converter(connection);

			return null;
		}
	}
}
