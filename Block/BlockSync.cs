﻿using DLT.Meta;
using DLT.Network;
using IXICore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DLT
{


    class BlockSync
    {
        public bool synchronizing { get; private set; }
        List<Block> pendingBlocks = new List<Block>();
        List<ulong> missingBlocks = null;
        public ulong pendingWsBlockNum { get; private set; }
        readonly List<WsChunk> pendingWsChunks = new List<WsChunk>();
        int wsSyncCount = 0;
        DateTime lastChunkRequested;
        Dictionary<ulong, long> requestedBlockTimes = new Dictionary<ulong, long>();

        ulong lastBlockToReadFromStorage = 0;

        ulong syncTargetBlockNum;
        int maxBlockRequests = 50; // Maximum number of block requests per iteration
        bool receivedAllMissingBlocks = false;

        public ulong wsSyncConfirmedBlockNum = 0;
        int wsSyncConfirmedVersion;
        bool wsSynced = false;
        string syncNeighbor;
        HashSet<int> missingWsChunks = new HashSet<int>();

        bool canPerformWalletstateSync = false;

        private Thread sync_thread = null;

        private bool running = false;

        private ulong watchDogBlockNum = 0;
        private DateTime watchDogTime = DateTime.Now;

        public BlockSync()
        {
            synchronizing = false;
            receivedAllMissingBlocks = false;

            running = true;
            // Start the thread
            sync_thread = new Thread(onUpdate);
            sync_thread.Start();
        }

        public void onUpdate()
        {
            
            while (running)
            {
                if (synchronizing == false)
                {
                    Thread.Sleep(100);
                    continue;
                }
                if (syncTargetBlockNum == 0)
                {
                    // we haven't connected to any clients yet
                    Thread.Sleep(100);
                    continue;
                }

            //    Logging.info(String.Format("BlockSync: {0} blocks received, {1} walletstate chunks pending.",
              //      pendingBlocks.Count, pendingWsChunks.Count));
                if (!Config.storeFullHistory && !Config.recoverFromFile && wsSyncConfirmedBlockNum == 0)
                {
                    startWalletStateSync();
                    Thread.Sleep(1000);
                    continue;
                }
                if (Config.storeFullHistory || Config.recoverFromFile || (wsSyncConfirmedBlockNum > 0 && wsSynced))
                {
                    // Request missing blocks if needed
                    if (receivedAllMissingBlocks == false)
                    {
                        // Proceed with rolling forward the chain
                        rollForward();

                        if (requestMissingBlocks())
                        {
                            // If blocks were requested, wait for next iteration
                            continue;
                        }

                    }
                }
                // Check if we can perform the walletstate synchronization
                if (canPerformWalletstateSync)
                {
                    performWalletStateSync();
                    Thread.Sleep(1000);
                }
                else
                {
                    // Proceed with rolling forward the chain
                    rollForward();
                }
                Thread.Yield();
            }
        }

        public void stop()
        {
            running = false;
            Logging.info("BlockSync stopped.");
        }


        private bool requestMissingBlocks()
        {
            if (syncTargetBlockNum == 0)
            {
                return false;
            }

            if (requestedBlockTimes.Count > maxBlockRequests)
                return true;

            ulong syncToBlock = syncTargetBlockNum;

            ulong firstBlock = getLowestBlockNum();

            long currentTime = Core.getCurrentTimestamp();

            lock (pendingBlocks)
            {
                ulong lastBlock = syncToBlock;
                if (missingBlocks == null)
                {
                    missingBlocks = new List<ulong>(Enumerable.Range(0, (int)(lastBlock - firstBlock + 1)).Select(x => (ulong)x + firstBlock));
                }

                int count = 0;

                // whatever is left in missingBlocks is what we need to request
                Logging.info(String.Format("{0} blocks are missing before node is synchronized...", missingBlocks.Count()));
                if (missingBlocks.Count() == 0)
                {
                    receivedAllMissingBlocks = true;
                    return false;
                }

                List<ulong> tmpMissingBlocks = new List<ulong>(missingBlocks);

                foreach (ulong blockNum in tmpMissingBlocks)
                {
                    // Check if the block has already been requested
                    lock (requestedBlockTimes)
                    {
                        if (requestedBlockTimes.ContainsKey(blockNum))
                        {
                            // Check if the request expired (after 25 seconds)
                            if (requestedBlockTimes[blockNum] > currentTime + 25)
                            {
                                // Re-request block
                                if (ProtocolMessage.broadcastGetBlock(blockNum) == false)
                                {
                                    Logging.warn(string.Format("Failed to rebroadcast getBlock request for {0}", blockNum));
                                }
                                // Re-set the block request time
                                requestedBlockTimes[blockNum] = currentTime;
                                continue;
                            }
                            else
                            {
                                // Wait a bit before requesting this block again
                                continue;
                            }
                        }
                    }
                    bool readFromStorage = false;
                    if(blockNum < lastBlockToReadFromStorage)
                    {
                        readFromStorage = true;
                    }
                    // First check if the missing block can be found in storage
                    Block block = Node.blockChain.getBlock(blockNum, readFromStorage);
                    if (block != null)
                    {
                        Node.blockSync.onBlockReceived(block);
                    }
                    else
                    {
                        // Didn't find the block in storage, request it from the network
                        if (ProtocolMessage.broadcastGetBlock(blockNum) == false)
                        {
                            Logging.warn(string.Format("Failed to broadcast getBlock request for {0}", blockNum));
                        }

                        // Set the block request time
                        lock (requestedBlockTimes)
                        {
                            requestedBlockTimes.Add(blockNum, currentTime);
                        }
                    }

                    count++;
                    if (count >= maxBlockRequests) break;
                }
                if (count > 0)
                    return true;
            }

            return false;
        }

        private void performWalletStateSync()
        {
            Logging.info(String.Format("WS SYNC block: {0}", wsSyncConfirmedBlockNum));
            if (wsSyncConfirmedBlockNum > 0)
            {
                Logging.info(String.Format("We are synchronizing to block #{0}.", wsSyncConfirmedBlockNum));
                requestWalletChunks();
                if (missingWsChunks.Count == 0)
                {
                    Logging.info("All WalletState chunks have been received. Applying");
                    lock (pendingWsChunks)
                    {
                        if (pendingWsChunks.Count > 0)
                        {
                            Node.walletState.clear();
                            Node.walletState.version = wsSyncConfirmedVersion;
                            foreach (WsChunk c in pendingWsChunks)
                            {
                                Logging.info(String.Format("Applying chunk {0}.", c.chunkNum));
                                Node.walletState.setWalletChunk(c.wallets);
                            }
                            pendingWsChunks.Clear();
                            wsSynced = true;
                        }
                    }
                }
                else // misingWsChunks.Count > 0
                {
                    return;
                }
                Logging.info(String.Format("Verifying complete walletstate as of block #{0}", wsSyncConfirmedBlockNum));

                canPerformWalletstateSync = false;
            }
            else // wsSyncStartBlock == 0
            {
                Logging.info("WalletState is already synchronized. Skipping.");
            }
        }

        private ulong getLowestBlockNum()
        {
            ulong lowestBlockNum = 1;

            ulong syncToBlock = syncTargetBlockNum;

            if (syncToBlock > CoreConfig.redactedWindowSize)
            {
                lowestBlockNum = syncToBlock - CoreConfig.redactedWindowSize + 1;
                if (wsSyncConfirmedBlockNum > 0 && wsSyncConfirmedBlockNum < lowestBlockNum)
                {
                    if (wsSyncConfirmedBlockNum > CoreConfig.redactedWindowSize)
                    {
                        lowestBlockNum = wsSyncConfirmedBlockNum - CoreConfig.redactedWindowSize + 1;
                    }
                    else
                    {
                        lowestBlockNum = 1;
                    }
                }else if(wsSyncConfirmedBlockNum == 0)
                {
                    lowestBlockNum = 1;
                }
            }
            return lowestBlockNum;
        }

        private void rollForward()
        {
            bool sleep = false;

            ulong lowestBlockNum = getLowestBlockNum();

            ulong syncToBlock = syncTargetBlockNum;

            if (Node.blockChain.Count > 0)
            {
                lock (pendingBlocks)
                {
                    pendingBlocks.RemoveAll(x => x.blockNum < Node.blockChain.getLastBlockNum() - 5);
                }
            }

            lock (pendingBlocks)
            {

                // Loop until we have no more pending blocks
                while (pendingBlocks.Count > 0)
                {
                    handleWatchDog();

                    ulong next_to_apply = lowestBlockNum;
                    if (Node.blockChain.Count > 0)
                    {
                        next_to_apply = Node.blockChain.getLastBlockNum() + 1;
                    }

                    if (next_to_apply > syncToBlock)
                    {
                        // we have everything, clear pending blocks and break
                        pendingBlocks.Clear();
                        break;
                    }
                    Block b = pendingBlocks.Find(x => x.blockNum == next_to_apply);
                    if (b == null)
                    {
                        if (!missingBlocks.Contains(next_to_apply))
                        {
                            Logging.info(String.Format("Requesting missing block #{0}", next_to_apply));
                            ProtocolMessage.broadcastGetBlock(next_to_apply);
                            sleep = true;
                        }
                        break;
                    }



                    ulong targetBlock = next_to_apply - 5;

                    Block tb = pendingBlocks.Find(x => x.blockNum == targetBlock);
                    if (tb != null)
                    {
                        if (tb.blockChecksum.SequenceEqual(Node.blockChain.getBlock(tb.blockNum).blockChecksum) && Node.blockProcessor.verifyBlockBasic(tb) == BlockVerifyStatus.Valid)
                        {
                            Node.blockChain.refreshSignatures(tb, true);
                            Node.blockProcessor.handleSigFreezedBlock(tb);
                        }
                        pendingBlocks.RemoveAll(x => x.blockNum == tb.blockNum);
                    }

                    try
                    {

                        b.powField = null;

                        Logging.info(String.Format("Applying pending block #{0}. Left to apply: {1}.",
                            b.blockNum, syncToBlock - Node.blockChain.getLastBlockNum()));

                        bool ignoreWalletState = true;

                        if (b.blockNum > wsSyncConfirmedBlockNum)
                        {
                            ignoreWalletState = false;
                        }


                        // wallet state is correct as of wsConfirmedBlockNumber, so before that we call
                        // verify with a parameter to ignore WS tests, but do all the others
                        BlockVerifyStatus b_status = BlockVerifyStatus.Valid;

                        if (b.blockNum > lastBlockToReadFromStorage)
                        {
                            b_status = Node.blockProcessor.verifyBlock(b, ignoreWalletState);
                        }
                        else
                        {
                            foreach (string txid in b.transactions)
                            {
                                Transaction t = TransactionPool.getTransaction(txid, true);
                                if (t != null)
                                {
                                    TransactionPool.addTransaction(t, true, null, false);
                                }
                            }
                        }

                        if (b_status == BlockVerifyStatus.Indeterminate)
                        {
                            Logging.info(String.Format("Waiting for missing transactions from block #{0}...", b.blockNum));
                            Thread.Sleep(100);
                            return;
                        }
                        if (b_status == BlockVerifyStatus.Invalid)
                        {
                            Logging.warn(String.Format("Block #{0} is invalid. Discarding and requesting a new one.", b.blockNum));
                            pendingBlocks.RemoveAll(x => x.blockNum == b.blockNum);
                            ProtocolMessage.broadcastGetBlock(b.blockNum);
                            return;
                        }

                        // TODO: carefully verify this
                        // Apply transactions when rolling forward from a recover file without a synced WS
                        if (b.blockNum > wsSyncConfirmedBlockNum)
                        {
                            Node.blockProcessor.applyAcceptedBlock(b);
                            byte[] wsChecksum = Node.walletState.calculateWalletStateChecksum();
                            if (wsChecksum == null || !wsChecksum.SequenceEqual(b.walletStateChecksum))
                            {
                                Logging.error(String.Format("After applying block #{0}, walletStateChecksum is incorrect!. Block's WS: {1}, actual WS: {2}", b.blockNum, Crypto.hashToString(b.walletStateChecksum), Crypto.hashToString(wsChecksum)));
                                synchronizing = false;
                                return;
                            }
                            if (b.blockNum % 1000 == 0)
                            {
                                DLT.Meta.WalletStateStorage.saveWalletState(b.blockNum);
                            }
                        }
                        else
                        {
                            if (syncToBlock == b.blockNum)
                            {
                                byte[] wsChecksum = Node.walletState.calculateWalletStateChecksum();
                                if (wsChecksum == null || !wsChecksum.SequenceEqual(b.walletStateChecksum))
                                {
                                    Logging.warn(String.Format("Block #{0} is last and has an invalid WSChecksum. Discarding and requesting a new one.", b.blockNum));
                                    lock (pendingBlocks)
                                    {
                                        pendingBlocks.RemoveAll(x => x.blockNum == b.blockNum);
                                    }
                                    ProtocolMessage.broadcastGetBlock(b.blockNum);
                                    return;
                                }
                            }
                        }
                        bool sigFreezeCheck = Node.blockProcessor.verifySignatureFreezeChecksum(b);
                        if (Node.blockChain.Count <= 5 || sigFreezeCheck)
                        {
                            //Logging.info(String.Format("Appending block #{0} to blockChain.", b.blockNum));
                            if (b.blockNum <= wsSyncConfirmedBlockNum)
                            {
                                TransactionPool.setAppliedFlagToTransactionsFromBlock(b);
                            }
                            Node.blockChain.appendBlock(b);
                            resetWatchDog(b);
                            missingBlocks.RemoveAll(x => x < b.blockNum);
                        }
                        else if (Node.blockChain.Count > 5 && !sigFreezeCheck)
                        {
                            // invalid sigfreeze, waiting for the correct block
                            pendingBlocks.RemoveAll(x => x.blockNum == b.blockNum);
                            return;
                        }
                    }
                    catch (Exception e)
                    {
                        Logging.error(String.Format("Exception occured while syncing block #{0}: {1}", b.blockNum, e));
                    }

                    pendingBlocks.RemoveAll(x => x.blockNum == b.blockNum);

                }
            }
            if (!sleep && Node.blockChain.getLastBlockNum() == syncToBlock)
            {
                verifyLastBlock();
                return;
            }
            
            if(sleep)
            {
                Thread.Sleep(500);
            }
        }

        private void startWalletStateSync()
        {
            HashSet<string> all_neighbors = new HashSet<string>(NetworkClientManager.getConnectedClients().Concat(NetworkServer.getConnectedClients(true)));
            if (all_neighbors.Count < 1)
            {
                Logging.info(String.Format("Wallet state synchronization from storage."));
                return;
            }

            Random r = new Random();
            syncNeighbor = all_neighbors.ElementAt(r.Next(all_neighbors.Count));
            Logging.info(String.Format("Starting wallet state synchronization from {0}", syncNeighbor));       
            ProtocolMessage.syncWalletStateNeighbor(syncNeighbor);
        }

        // Verify the last block we have
        private bool verifyLastBlock()
        {
            Block b = Node.blockChain.getBlock(Node.blockChain.getLastBlockNum());
            if(!b.walletStateChecksum.SequenceEqual(Node.walletState.calculateWalletStateChecksum()))
            {
                // TODO TODO TODO resync?
                Logging.error(String.Format("Wallet state synchronization failed, last block's WS checksum does not match actual WS Checksum, last block #{0}, wsSyncStartBlock: #{1}, block's WS: {2}, actual WS: {3}", Node.blockChain.getLastBlockNum(), wsSyncConfirmedBlockNum, Crypto.hashToString(b.walletStateChecksum), Crypto.hashToString(Node.walletState.calculateWalletStateChecksum())));
                return false;
            }

            stopSyncStartBlockProcessing();

            return true;
        }

        private void stopSyncStartBlockProcessing()
        {
            // if we reach here, we are synchronized
            synchronizing = false;

            Node.blockProcessor.firstBlockAfterSync = true;
            Node.blockProcessor.resumeOperation();

            lock(pendingBlocks)
            {
                pendingBlocks.Clear();
                missingBlocks.Clear();
                missingBlocks = null;
            }

            if (!Config.recoverFromFile)
            {
                ProtocolMessage.broadcastProtocolMessage(ProtocolMessageCode.getUnappliedTransactions, new byte[1], null, true);

                Node.miner.start();
            }

        }

        // Request missing walletstate chunks from network
        private void requestWalletChunks()
        {
            lock(missingWsChunks)
            {
                int count = 0;
                foreach(int c in missingWsChunks)
                {
                    bool request_sent = ProtocolMessage.getWalletStateChunkNeighbor(syncNeighbor, c);
                    if(request_sent == false)
                    {
                        Logging.warn(String.Format("Failed to request wallet chunk from {0}. Restarting WalletState synchronization.", syncNeighbor));
                        startWalletStateSync();
                        return;
                    }

                    count += 1;
                    if (count > maxBlockRequests) break;
                }
                if (count > 0)
                {
                    Logging.info(String.Format("{0} WalletState chunks are missing before WalletState is synchronized...", missingWsChunks.Count));
                }
                Thread.Sleep(2000);
            }
        }

        // Called when receiving a walletstate synchronization request
        public bool startOutgoingWSSync(RemoteEndpoint endpoint)
        {
            // TODO TODO TODO this function really should be done better

            if (synchronizing == true)
            {
                Logging.warn("Unable to perform outgoing walletstate sync until own blocksync is complete.");
                return false;
            }

            lock (pendingWsChunks)
            {
                if (wsSyncCount == 0 || (DateTime.Now - lastChunkRequested).TotalSeconds > 150)
                {
                    wsSyncCount = 0;
                    pendingWsBlockNum = Node.blockChain.getLastBlockNum();
                    pendingWsChunks.Clear();
                    pendingWsChunks.AddRange(
                        Node.walletState.getWalletStateChunks(CoreConfig.walletStateChunkSplit, Node.blockChain.getLastBlockNum())
                        );
                }
                wsSyncCount++;
            }
            Logging.info("Started outgoing WalletState Sync.");
            return true;
        }

        public void outgoingSyncComplete()
        {
            // TODO TODO TODO this function really should be done better

            if (wsSyncCount > 0)
            {
                wsSyncCount--;
                if (wsSyncCount == 0)
                {
                    pendingWsChunks.Clear();
                }
            }
            Logging.info("Outgoing WalletState Sync finished.");
        }

        // passing endpoint through here is an ugly hack, which should be removed once network code is refactored.
        public void onRequestWalletChunk(int chunk_num, RemoteEndpoint endpoint)
        {
            // TODO TODO TODO this function really should be done better
            if (synchronizing == true)
            {
                Logging.warn("Neighbor is requesting WalletState chunks, but we are synchronizing!");
                return;
            }
            lastChunkRequested = DateTime.Now;
            lock (pendingWsChunks)
            {
                if (chunk_num >= 0 && chunk_num < pendingWsChunks.Count)
                {
                    ProtocolMessage.sendWalletStateChunk(endpoint, pendingWsChunks[chunk_num]);
                    if (chunk_num + 1 == pendingWsChunks.Count)
                    {
                        outgoingSyncComplete();
                    }
                }
                else
                {
                    Logging.warn(String.Format("Neighbor requested an invalid WalletState chunk: {0}, but the pending array only has 0-{1}.",
                        chunk_num, pendingWsChunks.Count));
                }
            }
        }

        public void onWalletChunkReceived(WsChunk chunk)
        {
            if(synchronizing == false)
            {
                Logging.warn("Received WalletState chunk, but we are not synchronizing!");
                return;
            }
            lock(missingWsChunks)
            {
                if(missingWsChunks.Contains(chunk.chunkNum))
                {
                    pendingWsChunks.Add(chunk);
                    missingWsChunks.Remove(chunk.chunkNum);
                }
            }
        }

        public void onBlockReceived(Block b)
        {
            if (synchronizing == false) return;
            lock (pendingBlocks)
            {
                if (b.blockNum > syncTargetBlockNum)
                {
                    if (missingBlocks != null)
                    {
                        for (ulong i = 1; syncTargetBlockNum + i < b.blockNum; i++)
                        {
                            missingBlocks.Add(syncTargetBlockNum + i);
                        }
                        receivedAllMissingBlocks = false;
                    }
                    syncTargetBlockNum = b.blockNum;
                    return;
                }

                if (missingBlocks != null)
                {
                    missingBlocks.RemoveAll(x => x == b.blockNum);
                }

                int idx = pendingBlocks.FindIndex(x => x.blockNum == b.blockNum);
                if (idx > -1)
                {
                    pendingBlocks[idx] = b;
                }
                else // idx <= -1
                {
                    pendingBlocks.Add(b);
                }
            }

            // Remove from requestedblocktimes, as the block has been received 
            lock (requestedBlockTimes)
            {
                if (requestedBlockTimes.ContainsKey(b.blockNum))
                    requestedBlockTimes.Remove(b.blockNum);
            }
        }
        
        public void startSync()
        {
            // clear out current state
            lock (pendingBlocks)
            {
                pendingBlocks.Clear();
            }
            synchronizing = true;
            // select sync partner for walletstate
            receivedAllMissingBlocks = false;
        }

        public void onWalletStateHeader(int ws_version, ulong ws_block, long ws_count)
        {
            if(synchronizing == true && wsSyncConfirmedBlockNum == 0)
            {
                long chunks = ws_count / CoreConfig.walletStateChunkSplit;
                if(ws_count % CoreConfig.walletStateChunkSplit > 0)
                {
                    chunks += 1;
                }
                Logging.info(String.Format("WalletState Starting block: #{0}. Wallets: {1} ({2} chunks)", 
                    ws_block, ws_count, chunks));
                wsSyncConfirmedBlockNum = ws_block;
                wsSyncConfirmedVersion = ws_version;
                lock (missingWsChunks)
                {
                    missingWsChunks.Clear();
                    for (int i = 0; i < chunks; i++)
                    {
                        missingWsChunks.Add(i);
                    }
                }

                // We can perform the walletstate sync now
                canPerformWalletstateSync = true;
            }
        }

        public void onHelloDataReceived(ulong block_height, byte[] block_checksum, byte[] walletstate_checksum, int consensus, ulong last_block_to_read_from_storage = 0)
        {
            Logging.info("SYNC HEADER DATA");
            Logging.info(string.Format("\t|- Block Height:\t\t#{0}", block_height));
            Logging.info(string.Format("\t|- Block Checksum:\t\t{0}", Crypto.hashToString(block_checksum)));
            Logging.info(string.Format("\t|- WalletState checksum:\t{0}", Crypto.hashToString(walletstate_checksum)));
            Logging.info(string.Format("\t|- Currently reported consensus:\t{0}", consensus));

            if (synchronizing)
            {
                if (block_height > syncTargetBlockNum)
                {
                    Logging.info(String.Format("Sync target increased from {0} to {1}.",
                        syncTargetBlockNum, block_height));

                    Node.blockProcessor.highestNetworkBlockNum = block_height;

                    ulong firstBlock = Node.getLastBlockHeight();

                    lock (pendingBlocks)
                    {
                        for(ulong i = 1; syncTargetBlockNum + i < block_height; i++)
                        {
                            missingBlocks.Add(syncTargetBlockNum + i);
                        }
                        receivedAllMissingBlocks = false;
                        syncTargetBlockNum = block_height;
                    }

                }
            } else
            {
                if(Node.blockProcessor.operating == false)
                {
                    lastBlockToReadFromStorage = last_block_to_read_from_storage;
                    // This should happen when node first starts up.
                    Logging.info(String.Format("Network synchronization started. Target block height: #{0}.", block_height));

                    Node.blockProcessor.highestNetworkBlockNum = block_height;
                    syncTargetBlockNum = block_height;
                    if (Node.walletState.calculateWalletStateChecksum().SequenceEqual(walletstate_checksum))
                    {
                        wsSyncConfirmedBlockNum = block_height;
                        wsSynced = true;
                        wsSyncConfirmedVersion = Node.walletState.version;
                    }
                    startSync();
                }
            }
        }

        private void resetWatchDog(Block b)
        {
            watchDogBlockNum = b.blockNum;
            watchDogTime = DateTime.Now;
        }

        private void handleWatchDog()
        {
            if(watchDogBlockNum > 0)
            {
                if ((DateTime.Now - watchDogTime).TotalSeconds > 60) // stuck on the same block for 60 seconds
                {
                    watchDogBlockNum = 0;
                    if (wsSyncConfirmedBlockNum > 0)
                    {
                        lastBlockToReadFromStorage = WalletStateStorage.restoreWalletState(wsSyncConfirmedBlockNum - 1);
                    }else
                    {
                        lastBlockToReadFromStorage = WalletStateStorage.restoreWalletState();
                    }

                    Block b = Node.blockChain.getBlock(lastBlockToReadFromStorage, true);
                    if (!Node.walletState.calculateWalletStateChecksum().SequenceEqual(b.walletStateChecksum))
                    {
                        Logging.error("BlockSync WatchDog: Wallet state mismatch");
                        wsSyncConfirmedBlockNum = lastBlockToReadFromStorage - 1;
                        return;
                    }

                    for (ulong blockNum = Node.blockChain.getLastBlockNum(); blockNum > lastBlockToReadFromStorage; blockNum--)
                    { 
                        Node.blockChain.removeBlock(blockNum);
                    }
                    wsSyncConfirmedBlockNum = lastBlockToReadFromStorage;

                    lock (pendingBlocks)
                    {
                        ulong firstBlock = Node.getLastBlockHeight();
                        ulong lastBlock = syncTargetBlockNum;
                        if (missingBlocks == null)
                        {
                            missingBlocks = new List<ulong>(Enumerable.Range(0, (int)(lastBlock - firstBlock + 1)).Select(x => (ulong)x + firstBlock));
                        }
                        pendingBlocks.Clear();
                    }
                }
            }
        }
    }
}
