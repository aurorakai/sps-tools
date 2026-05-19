using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class DepthParameterDetectorDummySocket : MonoBehaviour
    {
        public List<DepthParameterDetectorDummyDepthAction> depthActions2 =
            new List<DepthParameterDetectorDummyDepthAction>();
    }

    public class DepthParameterDetectorDummyDepthAction
    {
        public DepthParameterDetectorDummyState actionSet =
            new DepthParameterDetectorDummyState();
    }

    public class DepthParameterDetectorDummyState
    {
        public List<object> actions = new List<object>();
    }

    public class FxFloatAction
    {
        public string name;
    }

    public class NonFloatAction
    {
        public string name;
    }

    public class DepthParameterDetectorTests
    {
        [Test]
        public void RemoveFxFloatFromSocket_RemovesOnlySelectedFxFloatAction()
        {
            var go = new GameObject("Socket");
            var socket = go.AddComponent<DepthParameterDetectorDummySocket>();
            var depthAction = new DepthParameterDetectorDummyDepthAction();
            depthAction.actionSet.actions.Add(new FxFloatAction { name = "Keep_Depth" });
            depthAction.actionSet.actions.Add(new NonFloatAction { name = "Remove_Depth" });
            depthAction.actionSet.actions.Add(new FxFloatAction { name = "Remove_Depth" });
            socket.depthActions2.Add(depthAction);

            try
            {
                bool removed = DepthParameterDetector.RemoveFxFloatFromSocket(
                    socket, "Remove_Depth");

                Assert.IsTrue(removed);
                Assert.AreEqual(1, socket.depthActions2.Count);
                Assert.AreEqual(2, socket.depthActions2[0].actionSet.actions.Count);
                Assert.IsInstanceOf<FxFloatAction>(
                    socket.depthActions2[0].actionSet.actions[0]);
                Assert.IsInstanceOf<NonFloatAction>(
                    socket.depthActions2[0].actionSet.actions[1]);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RemoveFxFloatFromSocket_RemovesEmptyDepthAction()
        {
            var go = new GameObject("Socket");
            var socket = go.AddComponent<DepthParameterDetectorDummySocket>();
            var depthAction = new DepthParameterDetectorDummyDepthAction();
            depthAction.actionSet.actions.Add(new FxFloatAction { name = "Only_Depth" });
            socket.depthActions2.Add(depthAction);

            try
            {
                bool removed = DepthParameterDetector.RemoveFxFloatFromSocket(
                    socket, "Only_Depth");

                Assert.IsTrue(removed);
                Assert.AreEqual(0, socket.depthActions2.Count);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RemoveFxFloatFromSocket_DoesNotRemoveEmptyActionWhenNameNotFound()
        {
            var go = new GameObject("Socket");
            var socket = go.AddComponent<DepthParameterDetectorDummySocket>();
            socket.depthActions2.Add(new DepthParameterDetectorDummyDepthAction());

            try
            {
                bool removed = DepthParameterDetector.RemoveFxFloatFromSocket(
                    socket, "Missing_Depth");

                Assert.IsFalse(removed);
                Assert.AreEqual(1, socket.depthActions2.Count);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
