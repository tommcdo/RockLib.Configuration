﻿using Microsoft.Extensions.Configuration;
using RockLib.Configuration.ObjectFactory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace RockLib.Configuration.ProxyFactory
{
    public static class ConfigurationProxyFactory
    {
        private const TypeAttributes _typeAttributes = TypeAttributes.NotPublic | TypeAttributes.Class | TypeAttributes.AutoClass | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit | TypeAttributes.AutoLayout;
        private const MethodAttributes _methodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Virtual;

        private static readonly ConcurrentDictionary<Type, Type> _proxyCache = new ConcurrentDictionary<Type, Type>();

        public static T CreateProxy<T>(this IConfiguration configuration, DefaultTypes defaultTypes = null, ValueConverters valueConverters = null) =>
            (T)configuration.CreateProxy(typeof(T), defaultTypes, valueConverters);

        public static object CreateProxy(this IConfiguration configuration, Type type, DefaultTypes defaultTypes = null, ValueConverters valueConverters = null)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (type == null) throw new ArgumentNullException(nameof(type));
            var proxyType = _proxyCache.GetOrAdd(type, CreateProxyType);
            return configuration.Create(proxyType, defaultTypes, valueConverters);
        }

        private static Type CreateProxyType(Type type)
        {
            ValidateType(type);
            var typeBuilder = GetTypeBuilder(type);

            var readonlyFields = new List<(FieldBuilder FieldBuilder, string PropertyName)>();

            foreach (var property in type.GetTypeInfo().GetProperties())
            {
                var backingFieldName = "<" + property.Name + ">k__BackingField";

                if (property.CanWrite)
                {
                    var fieldBuilder = typeBuilder.DefineField(backingFieldName, property.PropertyType, FieldAttributes.Private);
                    var propertyBuilder = typeBuilder.DefineProperty(property.Name, PropertyAttributes.HasDefault, property.PropertyType, null);
                    var getMethodBuilder = GetGetMethodBuilder(property.Name, property.PropertyType, typeBuilder, fieldBuilder);
                    var setMethodBuilder = GetSetMethodBuilder(property.Name, property.PropertyType, typeBuilder, fieldBuilder);
                    propertyBuilder.SetGetMethod(getMethodBuilder);
                    propertyBuilder.SetSetMethod(setMethodBuilder);
                }
                else
                {
                    var fieldBuilder = typeBuilder.DefineField(backingFieldName, property.PropertyType, FieldAttributes.Private | FieldAttributes.InitOnly);
                    var propertyBuilder = typeBuilder.DefineProperty(property.Name, PropertyAttributes.None, property.PropertyType, null);
                    var getMethodBuilder = GetGetMethodBuilder(property.Name, property.PropertyType, typeBuilder, fieldBuilder);
                    propertyBuilder.SetGetMethod(getMethodBuilder);
                    readonlyFields.Add((fieldBuilder, property.Name));
                }
            }

            var constructorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard,
                readonlyFields.Select(f => f.FieldBuilder.FieldType).ToArray());
            var il = constructorBuilder.GetILGenerator();
            for (int i = 0; i < readonlyFields.Count; i++)
            {
                constructorBuilder.DefineParameter(i + 1, ParameterAttributes.None, readonlyFields[i].PropertyName);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Stfld, readonlyFields[i].FieldBuilder);
            }

            il.Emit(OpCodes.Ret);

            return typeBuilder.CreateTypeInfo().AsType();
        }

        private static TypeBuilder GetTypeBuilder(Type type)
        {
            var assemblyName = "<" + type.Name + ">a__RockLibDynamicAssembly";
            var name = "<" + type.Name + ">c__RockLibProxyClass";
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");
            var typeBuilder = moduleBuilder.DefineType(name, _typeAttributes, typeof(object), new[] { type });
            return typeBuilder;
        }

        private static MethodBuilder GetGetMethodBuilder(string name, Type type,
            TypeBuilder tb, FieldBuilder fieldBuilder)
        {
            var getMethodBuilder = tb.DefineMethod("get_" + name, _methodAttributes, type, Type.EmptyTypes);
            var il = getMethodBuilder.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, fieldBuilder);

            il.Emit(OpCodes.Ret);
            return getMethodBuilder;
        }

        private static MethodBuilder GetSetMethodBuilder(string name, Type type,
            TypeBuilder tb, FieldBuilder fieldBuilder)
        {
            var setMethodBuilder = tb.DefineMethod("set_" + name, _methodAttributes, null, new[] { type });
            var il = setMethodBuilder.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, fieldBuilder);

            il.Emit(OpCodes.Ret);
            return setMethodBuilder;
        }

        private static void ValidateType(Type type)
        {
            if (!type.GetTypeInfo().IsInterface)
                throw new ArgumentException($"Cannot create proxy instance of non-interface type {type}.", nameof(type));

            foreach (var member in type.GetTypeInfo().GetMembers())
            {
                string errorMessage = null;
                switch (member)
                {
                    case MethodInfo m when !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") && !m.Name.StartsWith("add_") && !m.Name.StartsWith("remove_"):
                        errorMessage = $"Cannot create proxy {type} implementation: target interface cannot contain any methods. `{m}`";
                        break;
                    case EventInfo e:
                        errorMessage = $"Cannot create proxy {type} implementation: target interface cannot contain any events. `{e}`";
                        break;
                    case PropertyInfo p when p.CanRead && p.GetGetMethod().GetParameters().Length > 0 || p.CanWrite && p.GetSetMethod().GetParameters().Length > 1:
                        errorMessage = $"Cannot create proxy {type} implementation: target interface cannot contain any indexer properties. `{p.PropertyType.Name} this[{string.Join(", ", p.GetIndexParameters().Select(i => i.ParameterType.Name))}] {{ {(p.CanRead ? "get; " : "")} {(p.CanWrite ? "set; " : "")}}}`";
                        break;
                    case PropertyInfo p when !p.CanRead:
                        errorMessage = $"Cannot create proxy {type} implementation: target interface cannot contain write-only methods. `{p} {{ set; }}`";
                        break;
                }
                if (errorMessage != null)
                    throw new ArgumentException(errorMessage, nameof(type));
            }
        }
    }
}
