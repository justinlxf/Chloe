﻿using Chloe.DbExpressions;
using Chloe.Query.Mapping;
using Chloe.Query.QueryExpressions;
using Chloe.Query.Visitors;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Linq;
using Chloe.Core.Visitors;
using Chloe.Utility;
using Chloe.Infrastructure;
using Chloe.Descriptors;
using Chloe.Entity;

namespace Chloe.Query.QueryState
{
    abstract class QueryStateBase : IQueryState
    {
        ResultElement _resultElement;
        protected QueryStateBase(ResultElement resultElement)
        {
            this._resultElement = resultElement;
        }

        public virtual ResultElement Result { get { return this._resultElement; } }

        public virtual IQueryState Accept(WhereExpression exp)
        {
            ScopeParameterDictionary scopeParameters = this._resultElement.ScopeParameters.Clone(exp.Predicate.Parameters[0], this._resultElement.ResultModel);

            DbExpression whereCondition = FilterPredicateParser.Parse(exp.Predicate, scopeParameters, this._resultElement.ScopeTables);
            this._resultElement.AppendCondition(whereCondition);

            return this;
        }
        public virtual IQueryState Accept(OrderExpression exp)
        {
            if (exp.NodeType == QueryExpressionType.OrderBy || exp.NodeType == QueryExpressionType.OrderByDesc)
                this._resultElement.Orderings.Clear();

            DbOrdering ordering = ParseOrderExpression(exp);

            if (this._resultElement.InheritOrderings)
            {
                this._resultElement.Orderings.Clear();
                this._resultElement.InheritOrderings = false;
            }

            this._resultElement.Orderings.Add(ordering);

            return this;
        }
        public virtual IQueryState Accept(SelectExpression exp)
        {
            ResultElement result = this.CreateNewResult(exp.Selector);
            return this.CreateQueryState(result);
        }
        public virtual IQueryState Accept(SkipExpression exp)
        {
            SkipQueryState state = new SkipQueryState(this.Result, exp.Count);
            return state;
        }
        public virtual IQueryState Accept(TakeExpression exp)
        {
            TakeQueryState state = new TakeQueryState(this.Result, exp.Count);
            return state;
        }
        public virtual IQueryState Accept(AggregateQueryExpression exp)
        {
            List<DbExpression> dbArguments = new List<DbExpression>(exp.Arguments.Count);
            foreach (Expression argument in exp.Arguments)
            {
                var arg = (LambdaExpression)argument;
                ScopeParameterDictionary scopeParameters = this._resultElement.ScopeParameters.Clone(arg.Parameters[0], this._resultElement.ResultModel);

                var dbArgument = GeneralExpressionParser.Parse(arg, scopeParameters, this._resultElement.ScopeTables);
                dbArguments.Add(dbArgument);
            }

            DbAggregateExpression dbAggregateExp = new DbAggregateExpression(exp.ElementType, exp.Method, dbArguments);
            PrimitiveObjectModel resultModel = new PrimitiveObjectModel(exp.ElementType, dbAggregateExp);

            ResultElement result = new ResultElement(this._resultElement.ScopeParameters, this._resultElement.ScopeTables);

            result.ResultModel = resultModel;
            result.FromTable = this._resultElement.FromTable;
            result.AppendCondition(this._resultElement.Condition);

            AggregateQueryState state = new AggregateQueryState(result);
            return state;
        }
        public virtual IQueryState Accept(GroupingQueryExpression exp)
        {
            foreach (LambdaExpression item in exp.GroupKeySelectors)
            {
                var keySelector = (LambdaExpression)item;
                ScopeParameterDictionary scopeParameters = this._resultElement.ScopeParameters.Clone(keySelector.Parameters[0], this._resultElement.ResultModel);

                this._resultElement.GroupSegments.AddRange(GroupKeySelectorParser.Parse(keySelector, scopeParameters, this._resultElement.ScopeTables));
            }

            foreach (LambdaExpression havingPredicate in exp.HavingPredicates)
            {
                ScopeParameterDictionary scopeParameters = this._resultElement.ScopeParameters.Clone(havingPredicate.Parameters[0], this._resultElement.ResultModel);

                var havingCondition = FilterPredicateParser.Parse(havingPredicate, scopeParameters, this._resultElement.ScopeTables);
                this._resultElement.AppendHavingCondition(havingCondition);
            }

            if (exp.Orderings.Count > 0)
            {
                this._resultElement.Orderings.Clear();
                this._resultElement.InheritOrderings = false;

                for (int i = 0; i < exp.Orderings.Count; i++)
                {
                    GroupingQueryOrdering groupOrdering = exp.Orderings[i];

                    ScopeParameterDictionary scopeParameters = this._resultElement.ScopeParameters.Clone(groupOrdering.KeySelector.Parameters[0], this._resultElement.ResultModel);

                    DbExpression orderingDbExp = GeneralExpressionParser.Parse(groupOrdering.KeySelector, scopeParameters, this._resultElement.ScopeTables);

                    DbOrdering ordering = new DbOrdering(orderingDbExp, groupOrdering.OrderType);
                    this._resultElement.Orderings.Add(ordering);
                }
            }

            var newResult = this.CreateNewResult(exp.Selector);
            return new GroupingQueryState(newResult);
        }
        public virtual IQueryState Accept(DistinctExpression exp)
        {
            DistinctQueryState state = new DistinctQueryState(this.Result);
            return state;
        }
        public virtual IQueryState Accept(IncludeExpression exp)
        {
            IObjectModel owner = this._resultElement.ResultModel;
            NavigationNode navigationNode = exp.NavigationChain;
            while (navigationNode != null)
            {
                TypeDescriptor ownerDescriptor = EntityTypeContainer.GetDescriptor(owner.ObjectType);
                PropertyDescriptor navigationDescriptor = ownerDescriptor.GetPropertyDescriptor(navigationNode.Property);

                if (navigationDescriptor.Definition.Kind == TypeKind.Primitive)
                {
                    throw new NotSupportedException($"{navigationNode.Property.Name} is not navigation property.");
                }

                if (navigationDescriptor.Definition.Kind == TypeKind.Complex)
                {
                    TypeDescriptor navigationTypeDescriptor = EntityTypeContainer.GetDescriptor(navigationDescriptor.PropertyType);
                    ComplexObjectModel objectModel = new ComplexObjectModel(navigationTypeDescriptor.GetDefaultConstructor());
                    owner.AddComplexMember(navigationDescriptor.Property, objectModel);

                    objectModel.Condition = FilterPredicateParser.Parse(navigationNode.Condition, new ScopeParameterDictionary(1) { { navigationNode.Condition.Parameters[0], objectModel } }, this._resultElement.ScopeTables);
                    objectModel.Filter = FilterPredicateParser.Parse(navigationNode.Filter, new ScopeParameterDictionary(1) { { navigationNode.Filter.Parameters[0], objectModel } }, this._resultElement.ScopeTables);

                    navigationNode = navigationNode.Next;
                    owner = objectModel;
                    continue;
                }

                Type collectionType = navigationDescriptor.PropertyType;
                TypeDescriptor elementTypeDescriptor = EntityTypeContainer.GetDescriptor(collectionType.GetGenericArguments()[0]);
                ComplexObjectModel elementModel = new ComplexObjectModel(elementTypeDescriptor.GetDefaultConstructor());
                DbTable table = new DbTable(this._resultElement.GenerateUniqueTableAlias(elementTypeDescriptor.Table.Name));
                foreach (PrimitivePropertyDescriptor propertyDescriptor in elementTypeDescriptor.PropertyDescriptors)
                {
                    DbColumnAccessExpression columnAccessExpression = new DbColumnAccessExpression(table, propertyDescriptor.Column);
                    elementModel.AddPrimitiveMember(propertyDescriptor.Property, columnAccessExpression);
                }

                CollectionObjectModel navModel = new CollectionObjectModel(navigationNode.Property.PropertyType, elementModel);
                owner.AddComplexMember(navigationNode.Property, navModel);

                elementModel.Condition = FilterPredicateParser.Parse(navigationNode.Condition, new ScopeParameterDictionary(1) { { navigationNode.Condition.Parameters[0], elementModel } }, this._resultElement.ScopeTables);
                elementModel.Filter = FilterPredicateParser.Parse(navigationNode.Filter, new ScopeParameterDictionary(1) { { navigationNode.Filter.Parameters[0], elementModel } }, this._resultElement.ScopeTables);

                navigationNode = navigationNode.Next;
                owner = navModel;
            }

            throw new NotImplementedException();
        }

        public virtual ResultElement CreateNewResult(LambdaExpression selector)
        {
            ResultElement result = new ResultElement(this._resultElement.ScopeParameters, this._resultElement.ScopeTables);
            result.FromTable = this._resultElement.FromTable;

            ScopeParameterDictionary scopeParameters = this._resultElement.ScopeParameters.Clone(selector.Parameters[0], this._resultElement.ResultModel);

            IObjectModel r = SelectorResolver.Resolve(selector, scopeParameters, this._resultElement.ScopeTables);
            result.ResultModel = r;
            result.Orderings.AddRange(this._resultElement.Orderings);
            result.AppendCondition(this._resultElement.Condition);

            result.GroupSegments.AddRange(this._resultElement.GroupSegments);
            result.AppendHavingCondition(this._resultElement.HavingCondition);

            return result;
        }
        public virtual IQueryState CreateQueryState(ResultElement result)
        {
            return new GeneralQueryState(result);
        }

        public virtual MappingData GenerateMappingData()
        {
            MappingData data = new MappingData();

            DbSqlQueryExpression sqlQuery = this.CreateSqlQuery();

            var objectActivatorCreator = this._resultElement.ResultModel.GenarateObjectActivatorCreator(sqlQuery);

            data.SqlQuery = sqlQuery;
            data.ObjectActivatorCreator = objectActivatorCreator;

            return data;
        }

        public virtual GeneralQueryState AsSubQueryState()
        {
            DbSqlQueryExpression sqlQuery = this.CreateSqlQuery();
            DbSubQueryExpression subQuery = new DbSubQueryExpression(sqlQuery);

            ResultElement result = new ResultElement(this._resultElement.ScopeParameters, this._resultElement.ScopeTables);

            DbTableSegment tableSeg = new DbTableSegment(subQuery, result.GenerateUniqueTableAlias(), LockType.Unspecified);
            DbFromTableExpression fromTable = new DbFromTableExpression(tableSeg);

            result.FromTable = fromTable;

            DbTable table = new DbTable(tableSeg.Alias);

            //TODO 根据旧的生成新 MappingMembers
            IObjectModel newModel = this.Result.ResultModel.ToNewObjectModel(sqlQuery, table);
            result.ResultModel = newModel;

            //得将 subQuery.SqlQuery.Orders 告诉 以下创建的 result
            //将 orderPart 传递下去
            if (this.Result.Orderings.Count > 0)
            {
                for (int i = 0; i < this.Result.Orderings.Count; i++)
                {
                    DbOrdering ordering = this.Result.Orderings[i];
                    DbExpression orderingExp = ordering.Expression;

                    string alias = null;

                    DbColumnSegment columnExpression = sqlQuery.ColumnSegments.Find(a => DbExpressionEqualityComparer.EqualsCompare(orderingExp, a.Body));

                    // 对于重复的则不需要往 sqlQuery.Columns 重复添加了
                    if (columnExpression != null)
                    {
                        alias = columnExpression.Alias;
                    }
                    else
                    {
                        alias = Utils.GenerateUniqueColumnAlias(sqlQuery);
                        DbColumnSegment columnSeg = new DbColumnSegment(orderingExp, alias);
                        sqlQuery.ColumnSegments.Add(columnSeg);
                    }

                    DbColumnAccessExpression columnAccessExpression = new DbColumnAccessExpression(table, DbColumn.MakeColumn(orderingExp, alias));
                    result.Orderings.Add(new DbOrdering(columnAccessExpression, ordering.OrderType));
                }
            }

            result.InheritOrderings = true;

            GeneralQueryState queryState = new GeneralQueryState(result);
            return queryState;
        }
        public virtual DbSqlQueryExpression CreateSqlQuery()
        {
            DbSqlQueryExpression sqlQuery = new DbSqlQueryExpression();

            sqlQuery.Table = this._resultElement.FromTable;
            sqlQuery.Orderings.AddRange(this._resultElement.Orderings);
            sqlQuery.Condition = this._resultElement.Condition;

            sqlQuery.GroupSegments.AddRange(this._resultElement.GroupSegments);
            sqlQuery.HavingCondition = this._resultElement.HavingCondition;

            return sqlQuery;
        }

        protected DbOrdering ParseOrderExpression(OrderExpression orderExp)
        {
            ScopeParameterDictionary scopeParameters = this._resultElement.ScopeParameters.Clone(orderExp.KeySelector.Parameters[0], this._resultElement.ResultModel);

            DbExpression dbExpression = GeneralExpressionParser.Parse(orderExp.KeySelector, scopeParameters, this._resultElement.ScopeTables);
            DbOrderType orderType;
            if (orderExp.NodeType == QueryExpressionType.OrderBy || orderExp.NodeType == QueryExpressionType.ThenBy)
            {
                orderType = DbOrderType.Asc;
            }
            else if (orderExp.NodeType == QueryExpressionType.OrderByDesc || orderExp.NodeType == QueryExpressionType.ThenByDesc)
            {
                orderType = DbOrderType.Desc;
            }
            else
                throw new NotSupportedException(orderExp.NodeType.ToString());

            DbOrdering ordering = new DbOrdering(dbExpression, orderType);

            return ordering;
        }

        public virtual ResultElement ToFromQueryResult()
        {
            ResultElement result = new ResultElement(this._resultElement.ScopeParameters, this._resultElement.ScopeTables);

            string alias = result.GenerateUniqueTableAlias(UtilConstants.DefaultTableAlias);
            DbSqlQueryExpression sqlQuery = this.CreateSqlQuery();
            DbSubQueryExpression subQuery = new DbSubQueryExpression(sqlQuery);

            DbTableSegment tableSeg = new DbTableSegment(subQuery, alias, LockType.Unspecified);
            DbFromTableExpression fromTable = new DbFromTableExpression(tableSeg);

            DbTable table = new DbTable(tableSeg.Alias);
            IObjectModel newModel = this.Result.ResultModel.ToNewObjectModel(sqlQuery, table);

            result.FromTable = fromTable;
            result.ResultModel = newModel;
            return result;
        }

        public virtual JoinQueryResult ToJoinQueryResult(JoinType joinType, LambdaExpression conditionExpression, ScopeParameterDictionary scopeParameters, KeyDictionary<string> scopeTables, string tableAlias)
        {
            DbSqlQueryExpression sqlQuery = this.CreateSqlQuery();
            DbSubQueryExpression subQuery = new DbSubQueryExpression(sqlQuery);

            string alias = tableAlias;
            DbTableSegment tableSeg = new DbTableSegment(subQuery, alias, LockType.Unspecified);

            DbTable table = new DbTable(tableSeg.Alias);
            IObjectModel newModel = this.Result.ResultModel.ToNewObjectModel(sqlQuery, table);

            scopeParameters[conditionExpression.Parameters[conditionExpression.Parameters.Count - 1]] = newModel;

            DbExpression condition = GeneralExpressionParser.Parse(conditionExpression, scopeParameters, scopeTables);

            DbJoinTableExpression joinTable = new DbJoinTableExpression(joinType.AsDbJoinType(), tableSeg, condition);

            JoinQueryResult result = new JoinQueryResult();
            result.ResultModel = newModel;
            result.JoinTable = joinTable;
            return result;
        }
    }
}
