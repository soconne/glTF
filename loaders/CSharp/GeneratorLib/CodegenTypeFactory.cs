﻿using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using glTFLoader.Shared;
using Newtonsoft.Json.Linq;

namespace GeneratorLib
{
    public static class CodegenTypeFactory
    {
        private static Dictionary<long, string> s_enumMap;
        private static readonly object s_enumMapLock = new object();

        private static Dictionary<long, string> EnumMap
        {
            get
            {
                lock (s_enumMapLock)
                {
                    if (s_enumMap != null)
                    {
                        return s_enumMap;
                    }

                    s_enumMap = new Dictionary<long, string>();
                    var spec = new XmlDocument();
                    spec.LoadXml(new WebClient().DownloadString("https://cvs.khronos.org/svn/repos/ogl/trunk/doc/registry/public/api/gl.xml"));
                    ExtractEnumValues(s_enumMap, spec);

                    return s_enumMap;
                }
            }
        }

        public static CodegenType MakeCodegenType(string name, Schema schema)
        {
            if (schema.ReferenceType != null)
            {
                throw new InvalidOperationException("We don't support de-referencing here.");
            }

            if (!(schema.Type?.Length >= 1))
            {
                throw new InvalidOperationException("This schema does not represent a type");
            }

            if (schema.DictionaryValueType == null)
            {
                if (schema.Type.Length == 1 && !schema.Type[0].IsReference && schema.Type[0].Name == "array")
                {
                    return MakeArrayType(name, schema);
                }

                return MakeSingleValueType(name, schema);
            }

            if (schema.Type.Length == 1 && schema.Type[0].Name == "object")
            {
                return MakeDictionaryType(name, schema);
            }

            throw new InvalidOperationException();
        }

        private static CodegenType MakeSingleValueType(string name, Schema schema)
        {
            CodegenType returnType;
            if (schema.Minimum != null || schema.Maximum != null)
            {
                returnType = new CodegenType()
                {
                    Attributes = new CodeAttributeDeclarationCollection
                    {
                        new CodeAttributeDeclaration(
                            "Newtonsoft.Json.JsonConverterAttribute",
                            new[]
                            {
                                new CodeAttributeArgument(new CodeTypeOfExpression(typeof (NumberValidator))),
                                new CodeAttributeArgument(
                                    new CodeArrayCreateExpression(typeof (object), new CodeExpression[]
                                    {
                                        new CodePrimitiveExpression(schema.Minimum ?? 0),
                                        new CodePrimitiveExpression(schema.Maximum ?? 0),
                                        new CodePrimitiveExpression(schema.Minimum != null),
                                        new CodePrimitiveExpression(schema.Maximum != null),
                                        new CodePrimitiveExpression(schema.ExclusiveMinimum),
                                        new CodePrimitiveExpression(schema.ExclusiveMaximum),
                                    })
                                ),
                            }
                        )
                    }
                };
            }
            else
            {
                returnType = new CodegenType();
            }
            {

                if (schema.Type.Length > 1)
                {
                    returnType.CodeType = new CodeTypeReference(typeof(object));
                    return returnType;
                }

                var typeRef = schema.Type[0];
                if (typeRef.IsReference)
                {
                    throw new NotImplementedException();
                }

                if (typeRef.Name == "any")
                {
                    if (schema.Enum != null || schema.Default != null)
                    {
                        throw new NotImplementedException();
                    }

                    returnType.CodeType = new CodeTypeReference(typeof(object));
                    return returnType;
                }

                if (typeRef.Name == "object")
                {
                    if (schema.Enum != null || schema.HasDefaultValue())
                    {
                        throw new NotImplementedException();
                    }

                    if (schema.Title != null)
                    {
                        returnType.CodeType = new CodeTypeReference(Helpers.ParseTitle(schema.Title));
                        return returnType;
                    }
                    throw new NotImplementedException();
                }

                if (typeRef.Name == "number")
                {
                    if (schema.Enum != null)
                    {
                        throw new NotImplementedException();
                    }

                    if (schema.HasDefaultValue())
                    {
                        returnType.DefaultValue = new CodePrimitiveExpression((float)(double)schema.Default);
                    }
                    returnType.CodeType = new CodeTypeReference(typeof(float));
                    return returnType;
                }

                if (typeRef.Name == "string")
                {
                    if (schema.Enum != null)
                    {
                        var enumName = $"{name}Enum";
                        var enumType = new CodeTypeDeclaration()
                        {
                            IsEnum = true,
                            Attributes = MemberAttributes.Public,
                            Name = enumName
                        };

                        foreach (var value in (JArray)schema.Enum)
                        {
                            enumType.Members.Add(new CodeMemberField(enumName, (string)value));
                        }

                        returnType.DependentType = enumType;
                        returnType.CodeType = new CodeTypeReference(enumName);

                        if (schema.HasDefaultValue())
                        {
                            for (var i = 0; i < enumType.Members.Count; i++)
                            {
                                if (enumType.Members[i].Name == schema.Default.ToString())
                                {
                                    returnType.DefaultValue =
                                        new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(enumName),
                                            (string)schema.Default);

                                    return returnType;
                                }
                            }
                            throw new InvalidDataException("The default value is not in the enum list");
                        }

                        return returnType;
                    }

                    if (schema.HasDefaultValue())
                    {
                        returnType.DefaultValue = new CodePrimitiveExpression((string)schema.Default);
                    }
                    returnType.CodeType = new CodeTypeReference(typeof(string));
                    return returnType;
                }

                if (typeRef.Name == "integer")
                {
                    if (schema.Enum != null)
                    {
                        var enumName = $"{name}Enum";
                        var enumType = new CodeTypeDeclaration()
                        {
                            IsEnum = true,
                            Attributes = MemberAttributes.Public,
                            Name = enumName
                        };

                        string defaultItemName = null;

                        foreach (var value in (JArray)schema.Enum)
                        {
                            var longValue = (long)value;
                            var itemName = EnumMap[longValue];

                            enumType.Members.Add(new CodeMemberField(enumName, itemName)
                            {
                                InitExpression = new CodePrimitiveExpression((int)longValue)
                            });

                            if (schema.HasDefaultValue() && (long)schema.Default == longValue)
                            {
                                defaultItemName = itemName;
                            }
                        }

                        returnType.DependentType = enumType;
                        returnType.CodeType = new CodeTypeReference(enumName);

                        if (schema.HasDefaultValue())
                        {
                            if (defaultItemName == null)
                            {
                                throw new InvalidDataException("The default value is not in the enum list");
                            }

                            returnType.DefaultValue =
                                new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(enumName),
                                    defaultItemName);

                            return returnType;
                        }

                        return returnType;
                    }

                    if (schema.Default != null)
                    {
                        returnType.DefaultValue = new CodePrimitiveExpression((int)(long)schema.Default);
                    }

                    returnType.CodeType = new CodeTypeReference(typeof(int));
                    return returnType;
                }

                if (typeRef.Name == "boolean")
                {
                    if (schema.Enum != null)
                    {
                        throw new NotImplementedException();
                    }

                    if (schema.Default != null)
                    {
                        returnType.DefaultValue = new CodePrimitiveExpression((bool)schema.Default);
                    }
                    returnType.CodeType = new CodeTypeReference(typeof(bool));
                    return returnType;
                }

                throw new NotImplementedException(typeRef.Name);
            }
        }

        private static CodegenType MakeArrayType(string name, Schema schema)
        {
            if (!(schema.Items?.Type?.Length > 0))
            {
                throw new InvalidOperationException("Array type must contain an item type");
            }

            if (schema.Enum != null)
            {
                throw new NotImplementedException();
            }

            var returnType = new CodegenType()
            {
                Attributes = new CodeAttributeDeclarationCollection
                    {
                        new CodeAttributeDeclaration(
                            "Newtonsoft.Json.JsonConverterAttribute",
                            new [] {
                                new CodeAttributeArgument(new CodeTypeOfExpression(typeof(ArrayConverter))),
                                new CodeAttributeArgument(
                                    new CodeArrayCreateExpression(typeof(object), new CodeExpression[]
                                    {
                                        new CodePrimitiveExpression(schema.MinItems ?? -1),
                                        new CodePrimitiveExpression(schema.MaxItems ?? -1),
                                        new CodePrimitiveExpression(schema.Items.MinLength),
                                        new CodePrimitiveExpression(schema.Items.MaxLength),
                                    })
                                ),
                            }
                        )
                    }
            };

            if (schema.Items.Type.Length > 1)
            {
                returnType.CodeType = new CodeTypeReference(typeof(object[]));
                return returnType;
            }
            if (schema.Items.Type[0].Name == "boolean")
            {
                if (schema.HasDefaultValue())
                {
                    var defaultVauleArray = (JArray)schema.Default;
                    returnType.DefaultValue = new CodeArrayCreateExpression(typeof(bool), defaultVauleArray.Select(x => (CodeExpression)new CodePrimitiveExpression((bool)x)).ToArray());
                }
                returnType.CodeType = new CodeTypeReference(typeof(bool[]));
                return returnType;
            }
            if (schema.Items.Type[0].Name == "string")
            {
                if (schema.HasDefaultValue())
                {
                    var defaultVauleArray = (JArray)schema.Default;
                    returnType.DefaultValue = new CodeArrayCreateExpression(typeof(string), defaultVauleArray.Select(x => (CodeExpression)new CodePrimitiveExpression((string)x)).ToArray());
                }

                returnType.CodeType = new CodeTypeReference(typeof(string[]));
                return returnType;
            }
            if (schema.Items.Type[0].Name == "integer")
            {
                if (schema.HasDefaultValue())
                {
                    var defaultVauleArray = ((JArray)schema.Default).Select(x => (CodeExpression)new CodePrimitiveExpression((int)(long)x)).ToArray();
                    returnType.DefaultValue = new CodeArrayCreateExpression(typeof(int), defaultVauleArray);
                }
                returnType.CodeType = new CodeTypeReference(typeof(int[]));
                return returnType;
            }
            if (schema.Items.Type[0].Name == "number")
            {
                if (schema.HasDefaultValue())
                {
                    var defaultVauleArray = (JArray)schema.Default;
                    returnType.DefaultValue = new CodeArrayCreateExpression(typeof(float), defaultVauleArray.Select(x => (CodeExpression)new CodePrimitiveExpression((float)x)).ToArray());
                }
                returnType.CodeType = new CodeTypeReference(typeof(float[]));
                return returnType;
            }
            if (schema.Items.Type[0].Name == "object")
            {
                if (schema.HasDefaultValue())
                {
                    throw new NotImplementedException("Array of Objects has default value");
                }

                returnType.CodeType = new CodeTypeReference(typeof(object[]));
                return returnType;
            }

            throw new NotImplementedException("Array of " + schema.Items.Type[0].Name);
        }

        private static CodegenType MakeDictionaryType(string name, Schema schema)
        {
            var returnType = new CodegenType();

            if (schema.DictionaryValueType.Type.Length > 1)
            {
                returnType.CodeType = new CodeTypeReference(typeof(Dictionary<string, object>));
                return returnType;
            }

            if (schema.HasDefaultValue())
            {
                throw new NotImplementedException("Defaults for dictionaries are not yet supported");
            }

            if (schema.DictionaryValueType.Type[0].Name == "object")
            {

                if (schema.DictionaryValueType.Title != null)
                {
                    returnType.CodeType = new CodeTypeReference($"System.Collections.Generic.Dictionary<string, {Helpers.ParseTitle(schema.DictionaryValueType.Title)}>");
                    return returnType;
                }
                returnType.CodeType = new CodeTypeReference(typeof(Dictionary<string, object>));
                return returnType;
            }

            if (schema.DictionaryValueType.Type[0].Name == "string")
            {
                returnType.CodeType = new CodeTypeReference(typeof(Dictionary<string, string>));
                return returnType;
            }

            throw new NotImplementedException($"Dictionary<string,{schema.DictionaryValueType.Type[0].Name}> not yet implemented.");
        }

        public static void ExtractEnumValues(Dictionary<long, string> values, XmlNode parentNode)
        {
            foreach (var nodeObject in parentNode)
            {
                var node = (XmlNode)nodeObject;
                ExtractEnumValues(values, node);
                if (node.Name == "enum" && node.Attributes?.Count >= 2)
                {
                    string name = null;
                    long? value = null;
                    foreach (var attributeObject in node.Attributes)
                    {
                        var attribute = (XmlAttribute)attributeObject;
                        if (attribute.Name == "value")
                        {
                            long result;
                            value = long.TryParse(attribute.Value, out result) ? result : Convert.ToInt64(attribute.Value, 16);
                        }

                        if (attribute.Name == "name")
                        {
                            name = attribute.Value;
                        }
                    }

                    if (name != null && value != null)
                    {
                        values[value.Value] = name.TrimLeftSubstring("GL_");
                    }
                }
            }
        }
    }
}
