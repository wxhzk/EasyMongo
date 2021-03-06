﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Linq.Expressions;
using MongoDB.Driver;
using System.Diagnostics;
using MongoDB.Bson;

namespace EasyMongo
{
    internal class BinaryUpdate : IPropertyUpdate
    {
        public static BinaryUpdate Create(PropertyInfo property, BinaryExpression binaryExpr)
        {
            Debug.Assert(property != null, "property should not be null");
            Debug.Assert(binaryExpr != null, "binaryExpr should not be null");
            Debug.Assert(binaryExpr.Right is ConstantExpression, "binaryExpr.Right should be ConstantExpression");

            var opType = GetSupportedOpType(binaryExpr.NodeType);
            var constant = ((ConstantExpression)binaryExpr.Right).Value;

            return new BinaryUpdate(property, opType, constant);
        }

        public BinaryUpdate(PropertyInfo property, ExpressionType opType, object constant)
        {
            this.Property = property;
            this.OpType = opType;
            this.Constant = constant;
        }

        public PropertyInfo Property { get; private set; }
        public object Constant { get; private set; }
        public ExpressionType OpType { get; private set; }

        public void Fill(IPropertyUpdateOperator optr, BsonDocument doc)
        {
            switch (this.OpType)
            {
                case ExpressionType.Add:
                    optr.PutAddUpdate(doc, this.Constant);
                    break;
                case ExpressionType.Subtract:
                    optr.PutAddUpdate(doc, GetContrary(this.Constant));
                    break;
                default:
                    throw new NotSupportedException(this.OpType + " is not supported");
            }
        }

        public object GetContrary(object value)
        {
            if (value is int) return -(int)value;
            if (value is float) return -(float)value;
            if (value is double) return -(double)value;

            throw new NotSupportedException();
        }

        private static ExpressionType GetSupportedOpType(ExpressionType type)
        {
            switch (type)
            {
                case ExpressionType.Add:
                case ExpressionType.Subtract:
                    return type;
                default:
                    throw new NotSupportedException(type + "is not supported");
            }
        }
    }
}
