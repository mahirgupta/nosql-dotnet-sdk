/*-
 * Copyright (c) 2026 Oracle and/or its affiliates. All rights reserved.
 *
 * Licensed under the Universal Permissive License v 1.0 as shown at
 * https://oss.oracle.com/licenses/upl/
 */

namespace Oracle.NoSQL.SDK.Tests
{
    using System;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Oracle.NoSQL.SDK.Query;
    using BinaryProtocol = Oracle.NoSQL.SDK.BinaryProtocol.Protocol;
    using PlanSerializer = Oracle.NoSQL.SDK.Query.BinaryProtocol.PlanSerializer;

    [TestClass]
    public class QueryV6CompatibilityTests
    {
        [TestMethod]
        public void TestPreparedStatementUsesEachUnionBranch()
        {
            var statement = new PreparedStatement();
            var firstProxy = new byte[] { 1 };
            var secondProxy = new byte[] { 2 };

            statement.AddQueryBranch(new PreparedStatement.QueryBranch
            {
                ProxyStatement = firstProxy,
                Namespace = "firstNamespace",
                TableName = "firstTable"
            });
            statement.AddQueryBranch(new PreparedStatement.QueryBranch
            {
                ProxyStatement = secondProxy,
                Namespace = "secondNamespace",
                TableName = "secondTable"
            });

            CollectionAssert.AreEqual(firstProxy,
                statement.GetProxyStatement(0));
            CollectionAssert.AreEqual(secondProxy,
                statement.GetProxyStatement(1));
            Assert.AreEqual("firstNamespace", statement.GetNamespace(0));
            Assert.AreEqual("secondTable", statement.GetTableName(1));
            statement.SetQueryBranchStores(new[] { "storeOne", "storeTwo" });
            Assert.AreEqual("storeOne", statement.GetStoreName(0));
            Assert.AreEqual("storeTwo", statement.GetStoreName(1));
            Assert.ThrowsException<BadProtocolException>(() =>
                statement.SetQueryBranchStores(new[] { "onlyOne" }));
            Assert.ThrowsException<BadProtocolException>(() =>
                statement.GetProxyStatement(2));
        }

        [TestMethod]
        public void TestUnionBranchNamespaceOverridesQueryOptions()
        {
            var statement = new PreparedStatement();
            statement.AddQueryBranch(new PreparedStatement.QueryBranch
            {
                ProxyStatement = new byte[] { 1 },
                Namespace = "firstNamespace"
            });
            statement.AddQueryBranch(new PreparedStatement.QueryBranch
            {
                ProxyStatement = new byte[] { 2 },
                Namespace = "secondNamespace"
            });

            using var client = new NoSQLClient(new NoSQLConfig
            {
                ServiceType = ServiceType.CloudSim,
                Endpoint = "http://localhost:8080",
                Namespace = "configNamespace"
            });
            var request = new QueryRequest<RecordValue>(client, statement,
                new QueryOptions
                {
                    Namespace = "optionsNamespace"
                });

            request.UnionBranch = 0;
            Assert.AreEqual("firstNamespace", request.Namespace);
            request.UnionBranch = 1;
            Assert.AreEqual("secondNamespace", request.Namespace);
        }

        [TestMethod]
        public void TestQueryVersionFallbackFromV6ToV3()
        {
            var handler = new ProtocolHandler();

            Assert.AreEqual(QueryRequestBase.QueryV6, handler.QueryVersion);
            Assert.IsTrue(handler.DecrementQueryVersion(
                QueryRequestBase.QueryV6));
            Assert.AreEqual(QueryRequestBase.QueryV5, handler.QueryVersion);
            Assert.IsTrue(handler.DecrementQueryVersion(
                QueryRequestBase.QueryV5));
            Assert.AreEqual(QueryRequestBase.QueryV4, handler.QueryVersion);
            Assert.IsTrue(handler.DecrementQueryVersion(
                QueryRequestBase.QueryV4));
            Assert.AreEqual(QueryRequestBase.QueryV3, handler.QueryVersion);
            Assert.IsFalse(handler.DecrementQueryVersion(
                QueryRequestBase.QueryV3));
        }

        [TestMethod]
        public void TestArrayCollectRegrouping()
        {
            var normal = new CollectAggregator(false, false);
            normal.Aggregate(new StringValue("one"));
            Assert.AreEqual(1, normal.Result.AsArrayValue.Count);
            Assert.AreEqual("one", normal.Result.AsArrayValue[0].AsString);

            var regrouped = new CollectAggregator(false, true);
            regrouped.Aggregate(new ArrayValue
            {
                new StringValue("one"), new StringValue("two")
            });
            Assert.AreEqual(2, regrouped.Result.AsArrayValue.Count);
            Assert.AreEqual("one", regrouped.Result.AsArrayValue[0].AsString);
            Assert.AreEqual("two", regrouped.Result.AsArrayValue[1].AsString);

            var distinct = new CollectDistinctAggregator(false, false);
            distinct.Aggregate(new ArrayValue
            {
                new IntegerValue(1), new IntegerValue(2),
                new IntegerValue(1)
            });
            Assert.AreEqual(2, distinct.Result.AsArrayValue.Count);
            Assert.AreEqual(1, distinct.Result.AsArrayValue[0].AsInt32);
            Assert.AreEqual(2, distinct.Result.AsArrayValue[1].AsInt32);
        }

        [TestMethod]
        public void TestV6DriverOperatorDeserialization()
        {
            Assert.IsInstanceOfType(DeserializeV6Step(3, stream =>
            {
                BinaryProtocol.WriteBoolean(stream, false);
                WriteStepArray(stream, 1, WriteIntegerConstStep);
            }), typeof(ArrayConstructorStep));

            Assert.IsInstanceOfType(DeserializeV6Step(3, stream =>
            {
                BinaryProtocol.WriteBoolean(stream, false);
                WriteStepArray(stream, 0, WriteIntegerConstStep);
            }), typeof(ArrayConstructorStep));

            Assert.IsInstanceOfType(DeserializeV6Step(5, stream =>
            {
                BinaryProtocol.WriteUnpackedInt16(stream,
                    (short)QueryFuncCode.Equal);
                WriteIntegerConstStep(stream);
                WriteIntegerConstStep(stream);
            }), typeof(ValueCompareStep));

            Assert.IsInstanceOfType(DeserializeV6Step(7, stream =>
            {
                BinaryProtocol.WriteUnpackedInt16(stream,
                    (short)QueryFuncCode.And);
                WriteStepArray(stream, 1, WriteBooleanConstStep);
            }), typeof(AndOrStep));

            Assert.IsInstanceOfType(DeserializeV6Step(19, stream =>
            {
                WriteStepArray(stream, 1, WriteBooleanConstStep);
                WriteStepArray(stream, 1, WriteIntegerConstStep);
                WriteIntegerConstStep(stream);
            }), typeof(CaseStep));

            Assert.IsInstanceOfType(DeserializeV6Step(26, stream =>
            {
                BinaryProtocol.WriteUnpackedInt16(stream,
                    (short)QueryFuncCode.IsNull);
                WriteIntegerConstStep(stream);
            }), typeof(IsNullStep));

            Assert.IsInstanceOfType(DeserializeV6Step(48, stream =>
            {
                BinaryProtocol.WriteUnpackedInt16(stream,
                    (short)QueryFuncCode.SeqSum);
                WriteIntegerConstStep(stream);
            }), typeof(SeqAggregateStep));
        }

        [TestMethod]
        public void TestV6DriverOperatorsExecute()
        {
            using var client = new NoSQLClient(new NoSQLConfig
            {
                ServiceType = ServiceType.CloudSim,
                Endpoint = "http://localhost:8080"
            });
            var runtime = new QueryRuntime(client, new PreparedStatement
            {
                RegisterCount = 16,
                DriverQueryPlan = new ReceiveStep()
            });
            var comparison = new ValueCompareStep
            {
                ResultPosition = 0,
                FuncCode = QueryFuncCode.GreaterThan,
                LeftStep = IntegerConstant(1, 11),
                RightStep = IntegerConstant(2, 10)
            };
            var @case = new CaseStep
            {
                ResultPosition = 3,
                ConditionSteps = new PlanStep[] { comparison },
                ThenSteps = new PlanStep[] { IntegerConstant(4, 11) },
                ElseStep = IntegerConstant(5, 0)
            };
            var caseIterator = @case.CreateSyncIterator(runtime);
            Assert.IsTrue(caseIterator.Next());
            Assert.AreEqual(11, caseIterator.Result.AsInt32);

            var arrayIterator = new ArrayConstructorStep
            {
                ResultPosition = 6,
                ArgSteps = new PlanStep[] { IntegerConstant(7, 1) }
            }.CreateSyncIterator(runtime);
            Assert.IsTrue(arrayIterator.Next());
            Assert.AreEqual(1, arrayIterator.Result.AsArrayValue.Count);

            var emptyArrayIterator = new ArrayConstructorStep
            {
                ResultPosition = 8,
                ArgSteps = Array.Empty<PlanStep>()
            }.CreateSyncIterator(runtime);
            Assert.IsTrue(emptyArrayIterator.Next());
            Assert.AreEqual(0, emptyArrayIterator.Result.AsArrayValue.Count);

            var aggregateIterator = new SeqAggregateStep
            {
                ResultPosition = 9,
                FuncCode = QueryFuncCode.SeqSum,
                InputStep = IntegerConstant(10, 3)
            }.CreateSyncIterator(runtime);
            Assert.IsTrue(aggregateIterator.Next());
            Assert.AreEqual(DbType.Long, aggregateIterator.Result.DbType);
            Assert.AreEqual(3L, aggregateIterator.Result.AsInt64);

            var noInputIterator = new SeqAggregateStep
            {
                ResultPosition = 11,
                FuncCode = QueryFuncCode.SeqSum,
                InputStep = new FieldStep
                {
                    ResultPosition = 12,
                    FieldName = "missing",
                    InputStep = new ConstStep
                    {
                        ResultPosition = 13,
                        Value = new MapValue()
                    }
                }
            }.CreateSyncIterator(runtime);
            Assert.IsFalse(noInputIterator.Next());
        }

        [TestMethod]
        public void TestV6ComparisonAndAggregateMatchJavaSemantics()
        {
            using var client = new NoSQLClient(new NoSQLConfig
            {
                ServiceType = ServiceType.CloudSim,
                Endpoint = "http://localhost:8080"
            });
            var runtime = new QueryRuntime(client, new PreparedStatement
            {
                RegisterCount = 8,
                DriverQueryPlan = new ReceiveStep()
            });

            var nestedNullComparison = new ValueCompareStep
            {
                ResultPosition = 0,
                FuncCode = QueryFuncCode.Equal,
                LeftStep = new ConstStep
                {
                    ResultPosition = 1,
                    Value = new ArrayValue { FieldValue.Null }
                },
                RightStep = new ConstStep
                {
                    ResultPosition = 2,
                    Value = new ArrayValue { FieldValue.Null }
                }
            }.CreateSyncIterator(runtime);
            Assert.IsTrue(nestedNullComparison.Next());
            Assert.AreSame(FieldValue.Null, nestedNullComparison.Result);

            var inputValue = new IntegerValue(3);
            var sum = new SeqAggregateStep
            {
                ResultPosition = 3,
                FuncCode = QueryFuncCode.SeqSum,
                InputStep = new ConstStep
                {
                    ResultPosition = 4,
                    Value = inputValue
                }
            }.CreateSyncIterator(runtime);
            Assert.IsTrue(sum.Next());
            Assert.AreEqual(DbType.Long, sum.Result.DbType);
            Assert.AreEqual(3L, sum.Result.AsInt64);
            Assert.AreEqual(3, inputValue.AsInt32);
        }

        [TestMethod]
        public void TestV6PlanFormatterUsesJavaInputContainers()
        {
            var arrayPlan = PlanFormatter.Format(new ArrayConstructorStep
            {
                ResultPosition = 0,
                ArgSteps = new PlanStep[] { IntegerConstant(1, 1) }
            });
            StringAssert.Contains(arrayPlan, "\"input iterators\" : [");

            var comparisonPlan = PlanFormatter.Format(new ValueCompareStep
            {
                ResultPosition = 0,
                FuncCode = QueryFuncCode.Equal,
                LeftStep = IntegerConstant(1, 1),
                RightStep = IntegerConstant(2, 1)
            });
            StringAssert.Contains(comparisonPlan, "\"input iterator\" :");
        }

        [TestMethod]
        public void TestUnionRuntimeFreezesPerStoreTopology()
        {
            using var client = new NoSQLClient(new NoSQLConfig
            {
                ServiceType = ServiceType.CloudSim,
                Endpoint = "http://localhost:8080"
            });
            client.SetQueryTopology(new TopologyInfo(4, new[] { 1 },
                "storeOne"));
            client.SetQueryTopology(new TopologyInfo(7, new[] { 2, 3 },
                "storeTwo"));

            var statement = new PreparedStatement
            {
                RegisterCount = 1,
                DriverQueryPlan = new ReceiveStep()
            };
            statement.AddQueryBranch(new PreparedStatement.QueryBranch
            {
                ProxyStatement = new byte[] { 1 }, StoreName = "storeOne"
            });
            statement.AddQueryBranch(new PreparedStatement.QueryBranch
            {
                ProxyStatement = new byte[] { 2 }, StoreName = "storeTwo"
            });
            var runtime = new QueryRuntime(client, statement);

            runtime.ConstructionUnionBranch = 0;
            Assert.AreEqual(4, runtime.GetConstructionTopology().SequenceNumber);
            runtime.ConstructionUnionBranch = 1;
            Assert.AreEqual(7, runtime.GetConstructionTopology().SequenceNumber);
        }

        private static ConstStep IntegerConstant(int position, int value) =>
            new ConstStep
            {
                ResultPosition = position,
                Value = new IntegerValue(value)
            };

        private static PlanStep DeserializeV6Step(byte type,
            Action<MemoryStream> writeContent)
        {
            using var stream = new MemoryStream();
            BinaryProtocol.WriteByte(stream, type);
            WriteBase(stream);
            writeContent(stream);
            stream.Position = 0;
            return PlanSerializer.DeserializeStep(stream,
                QueryRequestBase.QueryV6);
        }

        private static void WriteStepArray(MemoryStream stream, int count,
            Action<MemoryStream> writeStep)
        {
            BinaryProtocol.WritePackedInt32(stream, count);
            for (var i = 0; i < count; i++)
            {
                writeStep(stream);
            }
        }

        private static void WriteIntegerConstStep(MemoryStream stream)
        {
            BinaryProtocol.WriteByte(stream, 0);
            WriteBase(stream);
            BinaryProtocol.WriteFieldValue(stream, new IntegerValue(1));
        }

        private static void WriteBooleanConstStep(MemoryStream stream)
        {
            BinaryProtocol.WriteByte(stream, 0);
            WriteBase(stream);
            BinaryProtocol.WriteFieldValue(stream, BooleanValue.True);
        }

        private static void WriteBase(MemoryStream stream)
        {
            BinaryProtocol.WriteUnpackedInt32(stream, 0);
            BinaryProtocol.WriteUnpackedInt32(stream, 0);
            for (var i = 0; i < 4; i++)
            {
                BinaryProtocol.WriteUnpackedInt32(stream, 0);
            }
        }
    }
}
