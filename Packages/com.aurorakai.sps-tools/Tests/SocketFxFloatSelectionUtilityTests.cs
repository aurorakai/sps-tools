using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class SocketFxFloatSelectionUtilityTests
    {
        [Test]
        public void GetEnabledDepthParameters_UsesSelectedFxFloatPerSocket()
        {
            var config = ScriptableObject.CreateInstance<BulgeConfig>();
            try
            {
                config.enabledSocketIndices = new List<int> { 0, 1 };

                var sockets = new List<DetectedSocket>
                {
                    new DetectedSocket
                    {
                        gameObjectPath = "Avatar/SocketA",
                        depthFxFloats = new List<string> { "Bad_Depth", "Good_Depth" }
                    },
                    new DetectedSocket
                    {
                        gameObjectPath = "Avatar/SocketB",
                        depthFxFloats = new List<string> { "Other_Depth" }
                    }
                };

                SocketFxFloatSelectionUtility.SetSelectedParameter(
                    config, sockets[0], "Good_Depth");

                var parameters = SocketFxFloatSelectionUtility.GetEnabledDepthParameters(
                    config, sockets);

                CollectionAssert.AreEqual(
                    new[] { "Good_Depth", "Other_Depth" }, parameters);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void GetSelectedParameter_FallsBackToFirstFxFloat_WhenSavedSelectionIsMissing()
        {
            var config = ScriptableObject.CreateInstance<BulgeConfig>();
            try
            {
                var socket = new DetectedSocket
                {
                    gameObjectPath = "Avatar/Socket",
                    depthFxFloats = new List<string> { "Current_Depth" }
                };

                config.socketFxFloatSelections.Add(new SocketFxFloatSelection
                {
                    socketPath = "Avatar/Socket",
                    parameterName = "Deleted_Depth"
                });

                string parameter = SocketFxFloatSelectionUtility.GetSelectedParameter(
                    config, socket);

                Assert.AreEqual("Current_Depth", parameter);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void MakeUniqueParameterName_AppendsSuffixWhenSuggestedNameExists()
        {
            var socket = new DetectedSocket
            {
                depthFxFloats = new List<string> { "SPS_Depth_Test", "SPS_Depth_Test_2" }
            };

            string name = SocketFxFloatSelectionUtility.MakeUniqueParameterName(
                "SPS_Depth_Test", socket);

            Assert.AreEqual("SPS_Depth_Test_3", name);
        }
    }
}
