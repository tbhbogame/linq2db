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
	using Expressions;
	using System.Reflection;

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

		// TODO: use wrapper
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
		protected bool SetField(Type fieldType, string dataTypeName, string methodName, bool throwException = true, Type? dataReaderType = null)
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

			ReaderExpressions[new ReaderInfo { FieldType = fieldType, DataTypeName = dataTypeName, DataReaderType = dataReaderType }] =
				Expression.Lambda(
					call,
					dataReaderParameter,
					indexParameter);

			return true;
		}

		protected void SetProviderField<TField>(string methodName, Type? dataReaderType = null)
		{
			SetProviderField(typeof(TField), methodName, dataReaderType);
		}

		protected void SetProviderField(Type fieldType, string methodName, Type? dataReaderType = null)
		{
			var dataReaderParameter = Expression.Parameter(DataReaderType, "r");
			var indexParameter      = Expression.Parameter(typeof(int), "i");

			ReaderExpressions[new ReaderInfo { ProviderFieldType = fieldType, DataReaderType = dataReaderType }] =
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
		protected void SetToTypeField(Type toType, string methodName, Type? dataReaderType = null)
		{
			var dataReaderParameter = Expression.Parameter(DataReaderType, "r");
			var indexParameter = Expression.Parameter(typeof(int), "i");

			ReaderExpressions[new ReaderInfo { ToType = toType, DataReaderType = dataReaderType }] =
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

		protected bool SetProviderField<TTo, TField>(string methodName, bool throwException = true, Type? dataReaderType = null)
		{
			return SetProviderField(typeof(TTo), typeof(TField), methodName, throwException, dataReaderType);
		}

		protected bool SetProviderField(Type toType, Type fieldType, string methodName, bool throwException = true, Type? dataReaderType = null)
		{
			var dataReaderParameter = Expression.Parameter(DataReaderType, "r");
			var indexParameter      = Expression.Parameter(typeof(int), "i");

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

			ReaderExpressions[new ReaderInfo { ToType = toType, ProviderFieldType = fieldType, DataReaderType = dataReaderType }] =
				Expression.Lambda(methodCall, dataReaderParameter, indexParameter);

			return true;
		}

		protected void SetTypeConversion<T>(Func<T, object> convertFunc)
		{
			MappingSchema.SetConverter(convertFunc);
		}

		protected void SetTypeConversion(Type type, LambdaExpression convertExpression)
		{
			if (convertExpression.Body.Type != typeof(object))
			{
				var body = convertExpression.Body.Unwrap();
				if (body.Type != typeof(object))
					body = Expression.Convert(body, typeof(object));

				convertExpression = Expression.Lambda(
					body,
					convertExpression.Parameters);
			}
			MappingSchema.SetConvertExpression(type, typeof(object), convertExpression);
		}

		protected void SetTypeConversion(Type type, string memberName)
		{
			var valueParam = Expression.Parameter(type, "v");
			var member = type.GetInstanceMemberEx(memberName)
				.Single(m =>
					m.IsMethodEx() && ((MethodInfo)m).GetParameters().Length == 0 ||
					!m.IsMethodEx());

			Expression memberExpression;
			if (member.IsMethodEx())
				memberExpression = Expression.Call(valueParam, (MethodInfo)member);
			else
				memberExpression = Expression.MakeMemberAccess(valueParam, member);

			var convertExpression = Expression.Lambda(memberExpression, valueParam);
			SetTypeConversion(type, convertExpression);
		}

		#endregion

		// that's fine, as TryGetValue and indexer are lock-free operations for ConcurrentDictionary
		// in general I don't expect more than one wrapper used (e.g. miniprofiler), still it's not a big deal
		// to support multiple wrappers
		private readonly IDictionary<Type, Func<IDbDataParameter, IDbDataParameter>?> _parameterConverters   = new ConcurrentDictionary<Type, Func<IDbDataParameter, IDbDataParameter>?>();
		private readonly IDictionary<Type, Func<IDbCommand, IDbCommand>?>             _commandConverters     = new ConcurrentDictionary<Type, Func<IDbCommand, IDbCommand>?>();
		private readonly IDictionary<Type, Func<IDbConnection, IDbConnection>?>       _connectionConverters  = new ConcurrentDictionary<Type, Func<IDbConnection, IDbConnection>?>();
		private readonly IDictionary<Type, Func<IDbTransaction, IDbTransaction>?>     _transactionConverters = new ConcurrentDictionary<Type, Func<IDbTransaction, IDbTransaction>?>();

		internal virtual IDbDataParameter? TryConvertParameter(Type expectedType, IDbDataParameter parameter, MappingSchema ms)
		{
			var parameterType = parameter.GetType();

			if (expectedType == parameterType)
				return parameter;

			if (!_parameterConverters.TryGetValue(parameterType, out var converter))
			{
				var converterExpr = ms.GetConvertExpression(parameterType, typeof(IDbDataParameter), false, false);
				if (converterExpr != null)
				{
					var param = Expression.Parameter(typeof(IDbDataParameter));
					converter = (Func<IDbDataParameter, IDbDataParameter>)Expression.Lambda(converterExpr.GetBody(Expression.Convert(param, parameterType)), param).Compile();

					// TODO: does it makes sense to lock on create here?
					_parameterConverters[parameterType] = converter;
				}
			}

			if (converter != null)
				return converter(parameter);

			return null;
		}

		internal virtual IDbCommand? TryConvertCommand(Type expectedType, IDbCommand command, MappingSchema ms)
		{
			var commandType = command.GetType();

			if (expectedType == commandType)
				return command;

			if (!_commandConverters.TryGetValue(commandType, out var converter))
			{
				var converterExpr = ms.GetConvertExpression(commandType, typeof(IDbCommand), false, false);
				if (converterExpr != null)
				{
					var param = Expression.Parameter(typeof(IDbCommand));
					converter = (Func<IDbCommand, IDbCommand>)Expression.Lambda(converterExpr.GetBody(Expression.Convert(param, commandType)), param).Compile();

					_commandConverters[commandType] = converter;
				}
			}

			if (converter != null)
				return converter(command);

			return null;
		}

		internal virtual IDbConnection? TryConvertConnection(Type expectedType, IDbConnection connection, MappingSchema ms)
		{
			var connType = connection.GetType();

			if (expectedType == connType)
				return connection;

			if (!_connectionConverters.TryGetValue(connType, out var converter))
			{
				var converterExpr = ms.GetConvertExpression(connType, typeof(IDbConnection), false, false);
				if (converterExpr != null)
				{
					var param = Expression.Parameter(typeof(IDbConnection));
					converter = (Func<IDbConnection, IDbConnection>)Expression.Lambda(converterExpr.GetBody(Expression.Convert(param, connType)), param).Compile();

					_connectionConverters[connType] = converter;
				}
			}

			if (converter != null)
				return converter(connection);

			return null;
		}

		internal virtual IDbTransaction? TryConvertTransaction(Type expectedType, IDbTransaction transaction, MappingSchema ms)
		{
			var transactionType = transaction.GetType();

			if (expectedType == transactionType)
				return transaction;

			if (!_transactionConverters.TryGetValue(transactionType, out var converter))
			{
				var converterExpr = ms.GetConvertExpression(transactionType, typeof(IDbTransaction), false, false);
				if (converterExpr != null)
				{
					var param = Expression.Parameter(typeof(IDbTransaction));
					converter = (Func<IDbTransaction, IDbTransaction>)Expression.Lambda(converterExpr.GetBody(Expression.Convert(param, transactionType)), param).Compile();

					_transactionConverters[transactionType] = converter;
				}
			}

			if (converter != null)
				return converter(transaction);

			return null;
		}
	}
}
