﻿using RaftCore.StateMachine;
using RaftCore.Connections;
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace RaftCore {
    public enum NodeState { Leader, Follower, Candidate, Stopped };

    public class RaftNode {
    	// TODO: Some of these should be stored in non-volatile storage
        public uint NodeId { get; }

        public RaftCoreStateMachine StateMachine { get; private set; }
        public RaftCluster Cluster { get; private set; }
        public List<LogEntry> Log { get; private set; }

        public NodeState NodeState { get; private set; } = NodeState.Stopped;

        public uint? LeaderId { get; private set; } = null;
        public uint? VotedFor { get; private set; } = null;
        public int VoteCount { get; private set; } = 0;
        public int CommitIndex { get; private set; } = -1;
        public int LastApplied { get; private set; } = 0;

        public int ElectionTimeoutMS { get; private set; } // 150-300ms
        private Timer electionTimer;
        private Timer heartbeatTimer;

        // Leaders' state
        public Dictionary<uint, int> NextIndex { get; }
        public Dictionary<uint, int> MatchIndex { get; }

        private int currentTerm = 0;
        public int CurrentTerm {
            get {
                return currentTerm;
            }
            // Resets LeaderId, VoteCount and VotedFor when it's a term increase
            // Updates current term to the given value
            private set {
                if (value > currentTerm) {
                    currentTerm = value;
                    LeaderId = null;
                    VoteCount = 0;
                    VotedFor = null;
                    NodeState = NodeState.Follower;
                }
            }
        }

        // ******************************************
        // *  Initialization/configuration methods  *
        // ******************************************

        public RaftNode(uint nodeId, RaftCoreStateMachine stateMachine) {
            this.NodeId = nodeId;
            this.StateMachine = stateMachine;
            this.Log = new List<LogEntry>();

            electionTimer = new Timer(TriggerElection);
            heartbeatTimer = new Timer(SendHeartbeats);

            NextIndex = new Dictionary<uint, int>();
            MatchIndex = new Dictionary<uint, int>();
        }

        public void Configure(RaftCluster cluster) {
            this.Cluster = cluster;
            this.ElectionTimeoutMS = Cluster.CalculateElectionTimeoutMS();
            this.NodeState = NodeState.Follower;
        }


        public void Run() {
            switch(this.NodeState) {
                case NodeState.Candidate:
                    StopHeartbeatTimer();
                    ResetElectionTimer();
                    StartElection();
                    break;
                case NodeState.Leader:
                    StopElectionTimer();
                    ResetHeartbeatTimer();
                    ResetLeaderState();
                    break;
                case NodeState.Follower:
                    StopHeartbeatTimer();
                    ResetElectionTimer();
                    break;
                case NodeState.Stopped:
                    StopHeartbeatTimer();
                    StopElectionTimer();
                    break;
            }
        }

        
        // Invoked by leader to replicate log entries; also used as heartbeat.
        // Receiver implementation
        // should term be out param?
        public Result<bool> AppendEntries(int term, uint leaderId, int prevLogIndex, int prevLogTerm, 
                                  List<LogEntry> entries, int leaderCommit) {
            if (NodeState == NodeState.Stopped) {
                return new Result<bool>(entries == null, CurrentTerm);
            }
            if (term < this.CurrentTerm) {
                Console.WriteLine("Received AppendEntries with outdated term. Declining.");
                return new Result<bool>(false, CurrentTerm);
            }

            // TODO: Delete
            // if (Log.Count > 0 && prevLogIndex >= Log.Count) {
            //     Console.WriteLine("Log doesn't contain an entry at prevLogIndex");
            //     return false; // So it doesn't throw an exception right below
            // }

            if (entries != null && Log.Count > 0 && Log[prevLogIndex].TermNumber != prevLogTerm) {
                // log doesn’t contain an entry at prevLogIndex
                // whose term matches prevLogTerm
                return new Result<bool>(false, CurrentTerm);
            }

            // If we get to here it means the one sending us a message is leader
            StopHeartbeatTimer();
            ResetElectionTimer();
            CurrentTerm = term;

            NodeState = NodeState.Follower;
            LeaderId = leaderId;

            if (entries != null)  {
                // If an existing entry conflicts with a new one (same index
                // but different terms), delete the existing entry and all that
                // follow it (§5.3)
                Log = Log.Take(entries[0].Index).ToList();

                // Append any new entries not already in the log
                Log.AddRange(entries);

                Console.WriteLine("Node " + NodeId + " appending new entry " + entries[0].Command);
            }
            else { // HEARTBEAT
                Console.WriteLine("Node " + NodeId + " received heartbeat from " + leaderId);
            }


            if (leaderCommit > CommitIndex) {
                //TODO: It gets here on heartbeats
                Console.WriteLine("Node " + NodeId + " applying entries");
                // Instead of doing maths with leaderCommit and CommitIndex, could:
                // If commitIndex > lastApplied:
                // increment lastApplied, apply log[lastApplied] to state machine
                var toApply = Log.Skip(CommitIndex + 1).Take(leaderCommit - CommitIndex).ToList();

                if (toApply.Count == 0) {
                    Console.WriteLine("Node " + NodeId + " failed applying entries");
                    return new Result<bool>(false, CurrentTerm);
                }

                // TODO: Delete commented out code
                // toApply.ForEach(x => Console.WriteLine(x.Command));
                toApply.ForEach(x => StateMachine.Apply(x.Command));

                CommitIndex = Math.Min(leaderCommit, Log[Log.Count - 1].Index);
                
                LastApplied = CommitIndex;
            }

            return new Result<bool>(true, CurrentTerm);
        }

        // Invoked by candidates to request a vote.
        // Return value of true means candidate received vote
        public Result<bool> RequestVote(int term, uint candidateId, int lastLogIndex, int lastLogTerm) {
            if (NodeState == NodeState.Stopped) return new Result<bool>(false, CurrentTerm);
            Console.WriteLine("Node " + candidateId + " is requesting vote from node " + NodeId);

            bool voteGranted = false;
            if (term < CurrentTerm) {
                return new Result<bool>(voteGranted, CurrentTerm);
            }

            StopHeartbeatTimer();
            ResetElectionTimer();
            CurrentTerm = term;

            if ((VotedFor == null || VotedFor == candidateId)
                && lastLogIndex >= Log.Count - 1
                && lastLogTerm >= GetLastLogTerm()) {
                voteGranted = true;
            }

            if (voteGranted) {
                VotedFor = candidateId;
            }

            return new Result<bool>(voteGranted, CurrentTerm);
        }


        // **************************
        // *  Called by the client  *
        // **************************
        public void MakeRequest(String command) {
            if (NodeState == NodeState.Leader) {
                Console.WriteLine("This node is the leader");
                
                var entry = new LogEntry(CurrentTerm, Log.Count, command);
                Log.Add(entry);

                // TODO: return result of the execution
            }
            else {
                // Wait until there is a leader (maybe itself)
                // Then redirect them the request
                do {
                    Thread.Sleep(500);
                } while (!LeaderId.HasValue);
                uint leader = LeaderId.Value;

                if (leader == NodeId) {
                    // Redirect to a random node
                    var randomNode = Cluster.GetNodeIdsExcept(NodeId)[0];
                    Cluster.RedirectRequestToNode(command, randomNode);
                }
                else {
                    Console.WriteLine("Redirecting to leader " + leader + " by " + NodeId);
                    Cluster.RedirectRequestToNode(command, leader);
                }
            }
        }

        public List<LogEntry> GetCommittedEntries() {
            return Log.Take(CommitIndex + 1).ToList();
        }

        // **********************
        // *  INTERNAL METHODS  *
        // **********************

        private void StartElection() {
            CurrentTerm++;

            // Vote for self
            VoteCount = 1;
            VotedFor = NodeId;

            // Start election
            Console.Out.WriteLine("A node has started an election: " + NodeId + " (term " + CurrentTerm + ")");

            var nodes = Cluster.GetNodeIdsExcept(NodeId);
            int votes = 0;

            Parallel.ForEach(nodes, nodeId => 
            {
                var res = Cluster.RequestVoteFrom(nodeId, CurrentTerm, NodeId, Log.Count - 1, GetLastLogTerm());

                CurrentTerm = res.Term;

                if (res.Value) {
                    Interlocked.Increment(ref votes);
                }
            });
            VoteCount += votes;

            Console.Out.WriteLine(VoteCount);

            if (VoteCount >= GetMajority()) {
                Console.Out.WriteLine("Leader!! : " + NodeId);
                LeaderId = NodeId;
                NodeState = NodeState.Leader;
                Run();
            }
        }

        private void ResetLeaderState() {
            NextIndex.Clear();
            MatchIndex.Clear();

            Cluster.GetNodeIdsExcept(NodeId).ForEach(x => {
                NextIndex[x] = Log.Count;
                MatchIndex[x] = 0;
            });
        }

        public void Restart() {
            Console.WriteLine("Restarting node " + NodeId);
            NodeState = NodeState.Follower;
            Run();
        }

        public void Stop() {
            Console.WriteLine("Bringing node " + NodeId + " down");
            NodeState = NodeState.Stopped;
            Run();
        }

        private int GetMajority() {
            double n = (Cluster.Size + 1) / 2;
            return (int) Math.Ceiling(n);
        }

        private void TriggerElection(object arg) {
            NodeState = NodeState.Candidate;
            Run();
        }

        private void StopHeartbeatTimer() {
            heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void StopElectionTimer() {
            electionTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void ResetElectionTimer() {
            if (NodeState != NodeState.Leader) {
                electionTimer.Change(ElectionTimeoutMS, ElectionTimeoutMS);
            }
        }

        private void ResetHeartbeatTimer() {
            if (NodeState == NodeState.Leader) {
                heartbeatTimer.Change(0, ElectionTimeoutMS/2);
            }
        }

        // Returns the last term in the log, or 0 if log is empty
        private int GetLastLogTerm() {
            return (Log.Count > 0) ? Log[Log.Count - 1].TermNumber : 0;
        }

        private void SendHeartbeats(object arg) {
            var nodes = Cluster.GetNodeIdsExcept(NodeId);

            Parallel.ForEach(nodes, nodeId => 
            {
                var prevLogIndex = Math.Max(0, NextIndex[nodeId] - 1);
                int prevLogTerm = (Log.Count > 0) ? prevLogTerm = Log[prevLogIndex].TermNumber : 0;

                List<LogEntry> entries;

                if (Log.Count > NextIndex[nodeId]) {
                    Console.WriteLine("Log Count: " + Log.Count + " -- Target node[nextIndex]: " + nodeId + " [" + NextIndex[nodeId] + "]");
                    entries = Log.Skip(NextIndex[nodeId]).ToList();
                    // entries = new List<LogEntry>() { prevEntry };
                }
                else {
                    // covers Log is empty or no new entries to replicate
                    entries = null;
                }

                var res = Cluster.SendAppendEntriesTo(nodeId, CurrentTerm, NodeId, prevLogIndex, prevLogTerm, entries, CommitIndex);

                CurrentTerm = res.Term;

                if (res.Value) {
                    if (entries != null) {
                        // Entry appended
                        Console.WriteLine("Successful AE to " + nodeId + ". Setting nextIndex to " + NextIndex[nodeId]);
                        NextIndex[nodeId] = Log.Count;
                        MatchIndex[nodeId] = Log.Count - 1;
                    }
                    // TODO: Common code for checking term in response
                    // Wrong. Heartbeat received should contain a term number
                    
                }
                else {
                    Console.WriteLine("Failed AE to " + nodeId + ". Setting nextIndex to " + NextIndex[nodeId]);
                    // Entry failed to be appended
                    if (NextIndex[nodeId] > 0) {
                        NextIndex[nodeId]--;
                    }

                }
            });

            // TODO: Do this as new task?
            // Iterate over all uncommitted entries
            for(int i = CommitIndex + 1; i < Log.Count; i++) {
                // We add 1 because we know the entry is replicated in this node
                var replicatedIn = MatchIndex.Values.Count(x => x >= i) + 1;
                if (Log[i].TermNumber == CurrentTerm && replicatedIn > GetMajority()) {
                    CommitIndex = i;
                    StateMachine.Apply(Log[i].Command);
                    LastApplied = i;
                }
            }
            // (responder a client request)

        }

        internal void TestConnection() {
            StateMachine.TestConnection();
        }

        public override string ToString() {
            string state;
            if (NodeState == NodeState.Follower)
                state = "Follower (of " + LeaderId + ")";
            else
                state = NodeState.ToString();
            return "Node (" + NodeId + ") -- " + state;
        }

    }
}
