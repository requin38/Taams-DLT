﻿using DLT.Meta;
using IXICore;
using IXICore.Meta;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DLT
{
    public enum BlockSearchMode
    {
        lowestDifficulty,
        randomLowestDifficulty,
        latestBlock,
        random
    }

    class Miner
    {
        public bool pause = true; // Flag to toggle miner activity

        public long lastHashRate = 0; // Last reported hash rate
        public ulong currentBlockNum = 0; // Mining block number
        public int currentBlockVersion = 0;
        public ulong currentBlockDifficulty = 0; // Current block difficulty
        public byte[] currentHashCeil { get; private set; }
        public ulong lastSolvedBlockNum = 0; // Last solved block number
        private DateTime lastSolvedTime = DateTime.MinValue; // Last locally solved block time
        public BlockSearchMode searchMode = BlockSearchMode.randomLowestDifficulty;

        private long hashesPerSecond = 0; // Total number of hashes per second
        private DateTime lastStatTime; // Last statistics output time
        private bool shouldStop = false; // flag to signal shutdown of threads
        private ThreadLiveCheck TLC;


        Block activeBlock = null;
        bool blockFound = false;

        byte[] activeBlockChallenge = null;

        private static Random random = new Random(); // used to seed initial curNonce's
        [ThreadStatic] private static byte[] curNonce = null; // Used for random nonce
        [ThreadStatic] private static byte[] dummyExpandedNonce = null;
        [ThreadStatic] private static int lastNonceLength = 0;

        private static List<ulong> solvedBlocks = new List<ulong>(); // Maintain a list of solved blocks to prevent duplicate work
        private static long solvedBlockCount = 0;


        public Miner()
        {
            lastStatTime = DateTime.UtcNow;

        }

        // Starts the mining threads
        public bool start()
        {
            if(Config.disableMiner)
                return false;

            // Calculate the allowed number of threads based on logical processor count
            Config.miningThreads = calculateMiningThreadsCount(Config.miningThreads);
            Logging.info(String.Format("Starting miner with {0} threads on {1} logical processors.", Config.miningThreads, Environment.ProcessorCount));

            shouldStop = false;

            TLC = new ThreadLiveCheck();
            // Start primary mining thread
            Thread miner_thread = new Thread(threadLoop);
            miner_thread.Name = "Miner_Main_Thread";
            miner_thread.Start();

            // Start secondary worker threads
            for (int i = 0; i < Config.miningThreads - 1; i++)
            {
                Thread worker_thread = new Thread(secondaryThreadLoop);
                worker_thread.Name = "Miner_Worker_Thread_#" + i.ToString();
                worker_thread.Start();
            }

            return true;
        }

        // Signals all the mining threads to stop
        public bool stop()
        {
            shouldStop = true;
            return true;
        }

        // Returns the allowed number of mining threads based on amount of logical processors detected
        public static uint calculateMiningThreadsCount(uint miningThreads)
        {
            uint vcpus = (uint)Environment.ProcessorCount;

            // Single logical processor detected, force one mining thread maximum
            if (vcpus <= 1)
            {
                Logging.info("Single logical processor detected, forcing one mining thread maximum.");
                return 1;
            }

            // Calculate the maximum number of threads allowed
            uint maxThreads = (vcpus / 2) - 1;
            if (maxThreads < 1)
            {
                return 1;
            }

            // Provided mining thread count exceeds maximum
            if (miningThreads > maxThreads)
            {
                Logging.warn("Provided mining thread count ({0}) exceeds maximum allowed ({1})", miningThreads, maxThreads);
                return maxThreads;
            }

            // Provided mining thread count is allowed
            return miningThreads;
        }

        public static byte[] getHashCeilFromDifficulty(ulong difficulty)
        {
            /*
             * difficulty is an 8-byte number from 0 to 2^64-1, which represents how hard it is to find a hash for a certain block
             * the dificulty is converted into a 'ceiling value', which specifies the maximum value a hash can have to be considered valid under that difficulty
             * to do this, follow the attached algorithm:
             *  1. calculate a bit-inverse value of the difficulty
             *  2. create a comparison byte array with the ceiling value of length 10 bytes
             *  3. set the first two bytes to zero
             *  4. insert the inverse difficulty as the next 8 bytes (mind the byte order!)
             *  5. the remaining 22 bytes are assumed to be 'FF'
             */
            byte[] hash_ceil = new byte[10];
            hash_ceil[0] = 0x00;
            hash_ceil[1] = 0x00;
            for(int i=0;i<8;i++)
            {
                int shift = 8 * (7 - i);
                ulong mask = ((ulong)0xff) << shift;
                byte cb = (byte)((difficulty & mask) >> shift);
                hash_ceil[i + 2] = (byte)~cb;
            }
            return hash_ceil;
        }

        public static BigInteger getTargetHashcountPerBlock(ulong difficulty)
        {
            // For difficulty calculations see accompanying TXT document in the IxianDLT folder.
            // I am sorry for this - Zagar
            // What it does:
            // internally (in Miner.cs), we use little-endian byte arrays to represent hashes and solution ceilings, because it is slightly more efficient memory-allocation-wise.
            // in this place, we are using BigInteger's division function, so we don't have to write our own.
            // BigInteger uses a big-endian byte-array, so we have to reverse our ceiling, which looks like this:
            // little endian: 0000 XXXX XXXX XXXX XXXX FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF ; X represents where we set the difficulty
            // big endian: FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF YYYY YYYY YYYY YYYY 0000 ; Y represents the difficulty, but the bytes are reversed
            // 9 -(i-22) transforms the index in the big-endian byte array into an index in our 'hash_ceil'. Please also note that due to effciency we only return the
            // "interesting part" of the hash_ceil (first 10 bytes) and assume the others to be FF when doing comparisons internally. The first part of the 'if' in the loop
            // fills in those bytes as well, because BigInteger needs them to reconstruct the number.
            byte[] hash_ceil = Miner.getHashCeilFromDifficulty(difficulty);
            byte[] full_ceil = new byte[32];
            // BigInteger requires bytes in big-endian order
            for (int i = 0; i < 32; i++)
            {
                if (i < 22)
                {
                    full_ceil[i] = 0xff;
                }
                else
                {
                    full_ceil[i] = hash_ceil[9 - (i - 22)];
                }
            }

            BigInteger ceil = new BigInteger(full_ceil);
            // the value below is the easiest way to get maximum hash value into a BigInteger (2^256 -1). Ixian shifts the integer 8 places to the right to get 8 decimal places.
            BigInteger max = new IxiNumber("1157920892373161954235709850086879078532699846656405640394575840079131.29639935").getAmount();
            return max / ceil;
        }

        /*public static ulong calculateEstimatedHashRate()
        {

        }*/

        public static ulong calculateTargetDifficulty(BigInteger current_hashes_per_block)
        {
            // Sorry :-)
            // Target difficulty is calculated as such:
            // We input the number of hashes that have been generated to solve a block (Network hash rate * 60 - we want that solving a block should take 60 seconds, if the entire network hash power was focused on one block, thus achieving
            // an approximate 50% solve rate).
            // We are using BigInteger for its division function, so that we don't have to write out own.
            // Dividing the max hash number with the hashrate will give us an appropriate ceiling, which would result in approximately one solution per "current_hashes_per_block" hash attempts.
            // This target ceiling contains our target difficulty, in the format:
            // big endian: FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF YYYY YYYY YYYY YYYY 0000; Y represents the difficulty, but the bytes are reversed
            // the bytes being reversed is actually okay, because we are using BitConverter.ToUInt64, which takes a big-endian byte array to return a ulong number.
            BigInteger max = new IxiNumber("1157920892373161954235709850086879078532699846656405640394575840079131.29639935").getAmount();
            if(current_hashes_per_block == 0)
            {
                current_hashes_per_block = 1000; // avoid divide by zero
            }
            BigInteger target_ceil = max / current_hashes_per_block;
            byte[] temp = target_ceil.ToByteArray();
            int temp_len = temp.Length;
            if(temp_len > 32)
            {
                temp_len = 32;
            }
            // we get the bytes in the reverse order, so the padding should go at the end
            byte[] target_ceil_bytes = new byte[32];
            Array.Copy(temp, target_ceil_bytes, temp_len);
            for (int i = temp_len; i < 32; i++)
            {
                target_ceil_bytes[i] = 0;
            }
            //
            byte[] difficulty = new byte[8];
            Array.Copy(target_ceil_bytes, 22, difficulty, 0, 8);
            for(int i = 0; i < 8; i++)
            {
                difficulty[i] = (byte)~difficulty[i];
            }
            return BitConverter.ToUInt64(difficulty, 0);
        }

        private void threadLoop(object data)
        {
            while (!shouldStop)
            {
                TLC.Report();
                Thread.Sleep(1000);

                // Wait for blockprocessor network synchronization
                if (Node.blockProcessor.operating == false)
                {
                    continue;
                }

                // Edge case for seeds
                if (Node.blockChain.getLastBlockNum() > 10)
                {
                    break;
                }
            }

            while (!shouldStop)
            {               
                if (pause)
                {
                    lastStatTime = DateTime.UtcNow;
                    lastHashRate = hashesPerSecond;
                    hashesPerSecond = 0;
                    Thread.Sleep(500);
                    continue;
                }

                if (blockFound == false)
                {
                    searchForBlock();
                }
                else
                {
                    if(currentBlockVersion < BlockVer.v5)
                        calculatePow_v2(currentHashCeil);
                    else
                        calculatePow_v3(currentHashCeil);

                }

                // Output mining stats
                TimeSpan timeSinceLastStat = DateTime.UtcNow - lastStatTime;
                if (timeSinceLastStat.TotalSeconds > 5)
                {
                    printMinerStatus();
                }
            }
        }

        private void secondaryThreadLoop(object data)
        {
            while (!shouldStop)
            {
                TLC.Report();
                Thread.Sleep(1000);

                // Wait for blockprocessor network synchronization
                if (Node.blockProcessor.operating == false)
                {
                    continue;
                }

                // Edge case for seeds
                if (Node.blockChain.getLastBlockNum() > 10)
                {
                    break;
                }
            }

            while (!shouldStop)
            {
                if (pause)
                {
                    Thread.Sleep(500);
                    continue;
                }

                if (blockFound == false)
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (currentBlockVersion < BlockVer.v5)
                    calculatePow_v2(currentHashCeil);
                else
                    calculatePow_v3(currentHashCeil);
            }
        }

        public void forceSearchForBlock()
        {
            blockFound = false;
        }

        public void checkActiveBlockSolved()
        {
            if (currentBlockNum > 0)
            {
                Block tmpBlock = Node.blockChain.getBlock(currentBlockNum, false, false);
                if (tmpBlock == null || tmpBlock.powField != null)
                {
                    blockFound = false;
                }
            }
        }

        // Static function used by the getMiningBlock API call
        public static Block getMiningBlock(BlockSearchMode searchMode)
        {
            Block candidate_block = null;

            List<Block> blockList = null;

            int block_offset = 1;
            if (Node.blockChain.Count >= (long)ConsensusConfig.getRedactedWindowSize())
            {
                block_offset = 1000;
            }

            if (searchMode == BlockSearchMode.lowestDifficulty)
            {
                blockList = Node.blockChain.getBlocks(block_offset, (int)Node.blockChain.Count - block_offset).Where(x => x.powField == null).OrderBy(x => x.difficulty).ToList();
            }
            else if (searchMode == BlockSearchMode.randomLowestDifficulty)
            {
                Random rnd = new Random();
                blockList = Node.blockChain.getBlocks(block_offset, (int)Node.blockChain.Count - block_offset).Where(x => x.powField == null).OrderBy(x => x.difficulty).Skip(rnd.Next(500)).ToList();
            }
            else if (searchMode == BlockSearchMode.latestBlock)
            {
                blockList = Node.blockChain.getBlocks(block_offset, (int)Node.blockChain.Count - block_offset).Where(x => x.powField == null).OrderByDescending(x => x.blockNum).ToList();
            }
            else if (searchMode == BlockSearchMode.random)
            {
                Random rnd = new Random();
                blockList = Node.blockChain.getBlocks(block_offset, (int)Node.blockChain.Count - block_offset).Where(x => x.powField == null).OrderBy(x => rnd.Next()).ToList();
            }
            // Check if the block list exists
            if (blockList == null)
            {
                Logging.error("No block list found while searching.");
                return null;
            }

            // Go through each block in the list
            foreach (Block block in blockList)
            {
                if (block.powField == null)
                {
                    ulong solved = 0;
                    lock (solvedBlocks)
                    {
                        solved = solvedBlocks.Find(x => x == block.blockNum);
                    }

                    // Check if this block is in the solved list
                    if (solved > 0)
                    {
                        // Do nothing at this point
                    }
                    else
                    {
                        // Block is not solved, select it
                        candidate_block = block;
                        break;
                    }
                }
            }

            return candidate_block;
        }

        // Returns the most recent block without a PoW flag in the redacted blockchain
        private void searchForBlock()
        {
            lock (solvedBlocks)
            {
                List<ulong> tmpSolvedBlocks = new List<ulong>(solvedBlocks);
                foreach (ulong blockNum in tmpSolvedBlocks)
                {
                    Block b = Node.blockChain.getBlock(blockNum, false, false);
                    if (b == null || b.powField != null)
                    {
                        solvedBlocks.Remove(blockNum);
                    }
                }
            }

            Block candidate_block = null;

            List<Block> blockList = null;

            int block_offset = 1;
            if(Node.blockChain.Count > (long)ConsensusConfig.getRedactedWindowSize())
            {
                block_offset = 1000;
            }

            if (searchMode == BlockSearchMode.lowestDifficulty)
            {
                blockList = Node.blockChain.getBlocks(block_offset, (int)Node.blockChain.Count - block_offset).Where(x => x.powField == null).OrderBy(x => x.difficulty).ToList();
            }
            else if (searchMode == BlockSearchMode.randomLowestDifficulty)
            {
                Random rnd = new Random();
                blockList = Node.blockChain.getBlocks(block_offset, (int)Node.blockChain.Count - block_offset).Where(x => x.powField == null).OrderBy(x => x.difficulty).Skip(rnd.Next(500)).ToList();
            }
            else if (searchMode == BlockSearchMode.latestBlock)
            {
                blockList = Node.blockChain.getBlocks(block_offset, (int)Node.blockChain.Count - block_offset).Where(x => x.powField == null).OrderByDescending(x => x.blockNum).ToList();
            }
            else if (searchMode == BlockSearchMode.random)
            {
                Random rnd = new Random();
                blockList = Node.blockChain.getBlocks(block_offset, (int)Node.blockChain.Count - block_offset).Where(x => x.powField == null).OrderBy(x => rnd.Next()).ToList();
            }

            // Check if the block list exists
            if (blockList == null)
            {
                Logging.error("No block list found while searching. Likely an incorrect miner block search mode.");
                return;
            }

            // Go through each block in the list
            foreach (Block block in blockList)
            {
                if (block.powField == null)
                {
                    ulong solved = 0;
                    lock(solvedBlocks)
                    {
                        solved = solvedBlocks.Find(x => x == block.blockNum);
                    }

                    // Check if this block is in the solved list
                    if (solved > 0)
                    {
                        // Do nothing at this point
                    }
                    else
                    {
                        // Block is not solved, select it
                        candidate_block = block;
                        break;
                    }
                }

            }

            if (candidate_block == null)
            {
                // No blocks with empty PoW field found, wait a bit
                Thread.Sleep(1000);
                return;
            }

            currentBlockNum = candidate_block.blockNum;
            currentBlockDifficulty = candidate_block.difficulty;
            currentBlockVersion = candidate_block.version;
            currentHashCeil = getHashCeilFromDifficulty(currentBlockDifficulty);

            activeBlock = candidate_block;
            byte[] block_checksum = activeBlock.blockChecksum;
            byte[] solver_address = Node.walletStorage.getPrimaryAddress();
            activeBlockChallenge = new byte[block_checksum.Length + solver_address.Length];
            System.Buffer.BlockCopy(block_checksum, 0, activeBlockChallenge, 0, block_checksum.Length);
            System.Buffer.BlockCopy(solver_address, 0, activeBlockChallenge, block_checksum.Length, solver_address.Length);

            blockFound = true;

            return;
        }

        private byte[] randomNonce(int length)
        {
            if (curNonce == null)
            {
                curNonce = new byte[length];
                lock (random)
                {
                    random.NextBytes(curNonce);
                }
            }
            bool inc_next = true;
            length = curNonce.Length;
            for (int pos = length - 1; inc_next == true && pos > 0; pos--)
            {
                if (curNonce[pos] < 0xFF)
                {
                    inc_next = false;
                    curNonce[pos]++;
                }
                else
                {
                    curNonce[pos] = 0;
                }
            }
            return curNonce;
        }

        // Expand a provided nonce up to expand_length bytes by appending a suffix of fixed-value bytes
        private static byte[] expandNonce(byte[] nonce, int expand_length)
        {
            if (dummyExpandedNonce == null)
            {
                dummyExpandedNonce = new byte[expand_length];
                for (int i = 0; i < dummyExpandedNonce.Length; i++)
                {
                    dummyExpandedNonce[i] = 0x23;
                }
            }

            // set dummy with nonce
            for (int i = 0; i < nonce.Length; i++)
            {
                dummyExpandedNonce[i] = nonce[i];
            }

            // clear any bytes from last nonce
            for(int i = nonce.Length; i < lastNonceLength; i++)
            {
                dummyExpandedNonce[i] = 0x23;
            }

            lastNonceLength = nonce.Length;

            return dummyExpandedNonce;
        }

        private void calculatePow_v1(byte[] hash_ceil)
        {
            // PoW = Argon2id( BlockChecksum + SolverAddress, Nonce)
            byte[] nonce = ASCIIEncoding.ASCII.GetBytes(ASCIIEncoding.ASCII.GetString(randomNonce(64)));
            byte[] hash = findHash_v1(activeBlockChallenge, nonce);
            if (hash.Length < 1)
            {
                Logging.error("Stopping miner due to invalid hash.");
                stop();
                return;
            }

            hashesPerSecond++;

            // We have a valid hash, update the corresponding block
            if (Miner.validateHashInternal_v1(hash, hash_ceil) == true)
            {
                Logging.info(String.Format("SOLUTION FOUND FOR BLOCK #{0}", activeBlock.blockNum));

                // Broadcast the nonce to the network
                sendSolution(nonce);

                // Add this block number to the list of solved blocks
                lock (solvedBlocks)
                {
                    solvedBlocks.Add(activeBlock.blockNum);
                    solvedBlockCount++;
                }

                lastSolvedBlockNum = activeBlock.blockNum;
                lastSolvedTime = DateTime.UtcNow;

                // Reset the block found flag so we can search for another block
                blockFound = false;
            }
        }

        private void calculatePow_v2(byte[] hash_ceil)
        {
            // PoW = Argon2id( BlockChecksum + SolverAddress, Nonce)
            byte[] nonce = randomNonce(64);
            byte[] hash = findHash_v1(activeBlockChallenge, nonce);
            if (hash.Length < 1)
            {
                Logging.error("Stopping miner due to invalid hash.");
                stop();
                return;
            }

            hashesPerSecond++;

            // We have a valid hash, update the corresponding block
            if (Miner.validateHashInternal_v2(hash, hash_ceil) == true)
            {
                Logging.info(String.Format("SOLUTION FOUND FOR BLOCK #{0}", activeBlock.blockNum));

                // Broadcast the nonce to the network
                sendSolution(nonce);

                // Add this block number to the list of solved blocks
                lock (solvedBlocks)
                {
                    solvedBlocks.Add(activeBlock.blockNum);
                    solvedBlockCount++;
                }

                lastSolvedBlockNum = activeBlock.blockNum;
                lastSolvedTime = DateTime.UtcNow;

                // Reset the block found flag so we can search for another block
                blockFound = false;
            }
        }


        private void calculatePow_v3(byte[] hash_ceil)
        {
            // PoW = Argon2id( BlockChecksum + SolverAddress, Nonce)
            byte[] nonce_bytes = randomNonce(64);
            byte[] fullnonce = expandNonce(nonce_bytes, 234236);

            byte[] hash = findHash_v2(activeBlockChallenge, fullnonce);
            if (hash.Length < 1)
            {
                Logging.error("Stopping miner due to invalid hash.");
                stop();
                return;
            }

            hashesPerSecond++;

            // We have a valid hash, update the corresponding block
            if (Miner.validateHashInternal_v2(hash, hash_ceil) == true)
            {
                Logging.info(String.Format("SOLUTION FOUND FOR BLOCK #{0}", activeBlock.blockNum));

                // Broadcast the nonce to the network
                sendSolution(nonce_bytes);

                // Add this block number to the list of solved blocks
                lock (solvedBlocks)
                {
                    solvedBlocks.Add(activeBlock.blockNum);
                    solvedBlockCount++;
                }

                lastSolvedBlockNum = activeBlock.blockNum;
                lastSolvedTime = DateTime.UtcNow;

                // Reset the block found flag so we can search for another block
                blockFound = false;
            }
        }

        // difficulty is number of consecutive starting bits which must be 0 in the calculated hash
        public static byte[] setDifficulty_v0(int difficulty)
        {
            if (difficulty < 14)
            {
                difficulty = 14;
            }
            if (difficulty > 256)
            {
                difficulty = 256;
            }
            List<byte> diff_temp = new List<byte>();
            while (difficulty >= 8)
            {
                diff_temp.Add(0xff);
                difficulty -= 8;
            }
            if (difficulty > 0)
            {
                byte lastbyte = (byte)(0xff << (8 - difficulty));
                diff_temp.Add(lastbyte);
            }
            return diff_temp.ToArray();
        }

        // Check if a hash is valid based on the current difficulty
        public static bool validateHash_v0(string hash, ulong difficulty = 0)
        {
            byte[] hashStartDifficulty = null;
            // Set the difficulty for verification purposes
            hashStartDifficulty = setDifficulty_v0((int)difficulty);

            if (hash.Length < hashStartDifficulty.Length)
            {
                return false;
            }

            for (int i = 0; i < hashStartDifficulty.Length; i++)
            {
                byte hash_byte = byte.Parse(hash.Substring(2 * i, 2), System.Globalization.NumberStyles.HexNumber);
                if ((hash_byte & hashStartDifficulty[i]) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool validateHashInternal_v1(byte[] hash, byte[] hash_ceil)
        {
            if(hash == null || hash.Length < 1)
            {
                return false;
            }
            for (int i = 0; i < hash.Length; i++)
            {
                byte cb = i < hash_ceil.Length ? hash_ceil[i] : (byte)0xff;
                if (hash_ceil[i] > hash[i]) return true;
                if (hash_ceil[i] < hash[i]) return false;
            }
            // if we reach this point, the hash is exactly equal to the ceiling we consider this a 'passing hash'
            return true;
        }

        private static bool validateHashInternal_v2(byte[] hash, byte[] hash_ceil)
        {
            if (hash == null || hash.Length < 32)
            {
                return false;
            }
            for (int i = 0; i < hash.Length; i++)
            {
                byte cb = i < hash_ceil.Length ? hash_ceil[i] : (byte)0xff;
                if (cb > hash[i]) return true;
                if (cb < hash[i]) return false;
            }
            // if we reach this point, the hash is exactly equal to the ceiling we consider this a 'passing hash'
            return true;
        }

        // Check if a hash is valid based on the current difficulty
        public static bool validateHash_v1(byte[] hash, ulong difficulty)
        {
            return validateHashInternal_v1(hash, getHashCeilFromDifficulty(difficulty));
        }

        public static bool validateHash_v2(byte[] hash, ulong difficulty)
        {
            return validateHashInternal_v2(hash, getHashCeilFromDifficulty(difficulty));
        }

        // Verify nonce
        public static bool verifyNonce_v0(string nonce, ulong block_num, byte[] solver_address, ulong difficulty)
        {
            if (nonce == null || nonce.Length < 1)
            {
                return false;
            }

            Block block = Node.blockChain.getBlock(block_num, false, false);
            if (block == null)
                return false;

            // TODO checksum the solver_address just in case it's not valid
            // also protect against spamming with invalid nonce/block_num
            Byte[] p1 = new Byte[block.blockChecksum.Length + solver_address.Length];
            System.Buffer.BlockCopy(block.blockChecksum, 0, p1, 0, block.blockChecksum.Length);
            System.Buffer.BlockCopy(solver_address, 0, p1, block.blockChecksum.Length, solver_address.Length);

            byte[] nonce_bytes = ASCIIEncoding.ASCII.GetBytes(nonce);
            string hash = Miner.findHash_v0(p1, nonce_bytes);

            if (Miner.validateHash_v0(hash, difficulty) == true)
            {
                // Hash is valid
                return true;
            }

            return false;
        }

        // Verify nonce
        public static bool verifyNonce_v1(string nonce, ulong block_num, byte[] solver_address, ulong difficulty)
        {
            if(nonce == null || nonce.Length < 1)
            {
                return false;
            }

            Block block = Node.blockChain.getBlock(block_num, false, false);
            if (block == null)
                return false;

            // TODO checksum the solver_address just in case it's not valid
            // also protect against spamming with invalid nonce/block_num
            byte[] p1 = new byte[block.blockChecksum.Length + solver_address.Length];
            System.Buffer.BlockCopy(block.blockChecksum, 0, p1, 0, block.blockChecksum.Length);
            System.Buffer.BlockCopy(solver_address, 0, p1, block.blockChecksum.Length, solver_address.Length);

            byte[] nonce_bytes = ASCIIEncoding.ASCII.GetBytes(nonce);
            byte[] hash = Miner.findHash_v1(p1, nonce_bytes);

            if (Miner.validateHash_v1(hash, difficulty) == true)
            {
                // Hash is valid
                return true;
            }

            return false;
        }

        // Verify nonce
        public static bool verifyNonce_v2(string nonce, ulong block_num, byte[] solver_address, ulong difficulty)
        {
            if (nonce == null || nonce.Length < 1 || nonce.Length > 128)
            {
                return false;
            }

            Block block = Node.blockChain.getBlock(block_num, false, false);
            if (block == null)
                return false;

            // TODO checksum the solver_address just in case it's not valid
            // also protect against spamming with invalid nonce/block_num
            byte[] p1 = new byte[block.blockChecksum.Length + solver_address.Length];
            System.Buffer.BlockCopy(block.blockChecksum, 0, p1, 0, block.blockChecksum.Length);
            System.Buffer.BlockCopy(solver_address, 0, p1, block.blockChecksum.Length, solver_address.Length);

            byte[] nonce_bytes = Crypto.stringToHash(nonce);
            byte[] hash = Miner.findHash_v1(p1, nonce_bytes);

            if (Miner.validateHash_v2(hash, difficulty) == true)
            {
                // Hash is valid
                return true;
            }

            return false;
        }

        // Verify nonce
        public static bool verifyNonce_v3(string nonce, ulong block_num, byte[] solver_address, ulong difficulty)
        {
            if (nonce == null || nonce.Length < 1 || nonce.Length > 128)
            {
                return false;
            }

            Block block = Node.blockChain.getBlock(block_num, false, false);
            if (block == null)
                return false;

            // TODO checksum the solver_address just in case it's not valid
            // also protect against spamming with invalid nonce/block_num
            byte[] p1 = new byte[block.blockChecksum.Length + solver_address.Length];
            System.Buffer.BlockCopy(block.blockChecksum, 0, p1, 0, block.blockChecksum.Length);
            System.Buffer.BlockCopy(solver_address, 0, p1, block.blockChecksum.Length, solver_address.Length);

            byte[] nonce_bytes = Crypto.stringToHash(nonce);
            byte[] fullnonce = expandNonce(nonce_bytes, 234236);
            byte[] hash = Miner.findHash_v2(p1, fullnonce);

            if (Miner.validateHash_v2(hash, difficulty) == true)
            {
                // Hash is valid
                return true;
            }

            return false;
        }

        // Submit solution with a provided blocknum
        // This is normally called from the API, as it is a static function
        public static bool sendSolution(byte[] nonce, ulong blocknum)
        {
            byte[] pubkey = Node.walletStorage.getPrimaryPublicKey();
            // Check if this wallet's public key is already in the WalletState
            Wallet mywallet = Node.walletState.getWallet(Node.walletStorage.getPrimaryAddress());
            if (mywallet.publicKey != null && mywallet.publicKey.SequenceEqual(pubkey))
            {
                // Walletstate public key matches, we don't need to send the public key in the transaction
                pubkey = null;
            }

            byte[] data = null;

            using (MemoryStream mw = new MemoryStream())
            {
                using (BinaryWriter writerw = new BinaryWriter(mw))
                {
                    writerw.Write(blocknum);
                    writerw.Write(Crypto.hashToString(nonce));                   
                    data = mw.ToArray();
                }
            }

            Transaction tx = new Transaction((int)Transaction.Type.PoWSolution, new IxiNumber(0), new IxiNumber(0), ConsensusConfig.ixianInfiniMineAddress, Node.walletStorage.getPrimaryAddress(), data, pubkey, Node.blockChain.getLastBlockNum());

            if (TransactionPool.addTransaction(tx))
            {
                PendingTransactions.addPendingLocalTransaction(tx);
            }
            else
            {
                Logging.error("An unknown error occured while sending API PoW solution.");
                return false;
            }

            return true;
        }

        // Broadcasts the solution to the network
        public void sendSolution(byte[] nonce)
        {
            byte[] pubkey = Node.walletStorage.getPrimaryPublicKey();
            // Check if this wallet's public key is already in the WalletState
            Wallet mywallet = Node.walletState.getWallet(Node.walletStorage.getPrimaryAddress());
            if (mywallet.publicKey != null && mywallet.publicKey.SequenceEqual(pubkey))
            {
                // Walletstate public key matches, we don't need to send the public key in the transaction
                pubkey = null;
            }

            byte[] data = null;

            using (MemoryStream mw = new MemoryStream())
            {
                using (BinaryWriter writerw = new BinaryWriter(mw))
                {
                    writerw.Write(activeBlock.blockNum);
                    writerw.Write(Crypto.hashToString(nonce));
                    data = mw.ToArray();
                }
            }

            Transaction tx = new Transaction((int)Transaction.Type.PoWSolution, new IxiNumber(0), new IxiNumber(0), ConsensusConfig.ixianInfiniMineAddress, Node.walletStorage.getPrimaryAddress(), data, pubkey, Node.blockChain.getLastBlockNum());

            if (TransactionPool.addTransaction(tx))
            {
                PendingTransactions.addPendingLocalTransaction(tx);
            }
            else
            {
                Logging.error("An unknown error occured while sending PoW solution.");
            }
        }

        private static string findHash_v0(byte[] data, byte[] salt)
        {
            string ret = "";
            try
            {
                byte[] hash = new byte[32];
                IntPtr data_ptr = Marshal.AllocHGlobal(data.Length);
                IntPtr salt_ptr = Marshal.AllocHGlobal(salt.Length);
                Marshal.Copy(data, 0, data_ptr, data.Length);
                Marshal.Copy(salt, 0, salt_ptr, salt.Length);
                UIntPtr data_len = (UIntPtr)data.Length;
                UIntPtr salt_len = (UIntPtr)salt.Length;
                IntPtr result_ptr = Marshal.AllocHGlobal(32);
                int result = NativeMethods.argon2id_hash_raw((UInt32)1, (UInt32)1024, (UInt32)4, data_ptr, data_len, salt_ptr, salt_len, result_ptr, (UIntPtr)32);
                Marshal.Copy(result_ptr, hash, 0, 32);
                ret = BitConverter.ToString(hash).Replace("-", string.Empty);
                Marshal.FreeHGlobal(data_ptr);
                Marshal.FreeHGlobal(result_ptr);
                Marshal.FreeHGlobal(salt_ptr);
            }
            catch (Exception e)
            {
                Logging.error(string.Format("Error during mining: {0}", e.Message));
            }
            return ret;
        }

        private static byte[] findHash_v1(byte[] data, byte[] salt)
        {
            try
            {
                byte[] hash = new byte[32];
                IntPtr data_ptr = Marshal.AllocHGlobal(data.Length);
                IntPtr salt_ptr = Marshal.AllocHGlobal(salt.Length);
                Marshal.Copy(data, 0, data_ptr, data.Length);
                Marshal.Copy(salt, 0, salt_ptr, salt.Length);
                UIntPtr data_len = (UIntPtr)data.Length;
                UIntPtr salt_len = (UIntPtr)salt.Length;
                IntPtr result_ptr = Marshal.AllocHGlobal(32);
                int result = NativeMethods.argon2id_hash_raw((UInt32)1, (UInt32)1024, (UInt32)2, data_ptr, data_len, salt_ptr, salt_len, result_ptr, (UIntPtr)32);
                Marshal.Copy(result_ptr, hash, 0, 32);
                Marshal.FreeHGlobal(data_ptr);
                Marshal.FreeHGlobal(result_ptr);
                Marshal.FreeHGlobal(salt_ptr);
                return hash;
            }
            catch(Exception e)
            {
                Logging.error(string.Format("Error during mining: {0}", e.Message));
                return null;
            }
        }

        private static byte[] findHash_v2(byte[] data, byte[] salt)
        {
            try
            {
                byte[] hash = new byte[32];
                IntPtr data_ptr = Marshal.AllocHGlobal(data.Length);
                IntPtr salt_ptr = Marshal.AllocHGlobal(salt.Length);
                Marshal.Copy(data, 0, data_ptr, data.Length);
                Marshal.Copy(salt, 0, salt_ptr, salt.Length);
                UIntPtr data_len = (UIntPtr)data.Length;
                UIntPtr salt_len = (UIntPtr)salt.Length;
                IntPtr result_ptr = Marshal.AllocHGlobal(32);
                int result = NativeMethods.argon2id_hash_raw((UInt32)2, (UInt32)2048, (UInt32)2, data_ptr, data_len, salt_ptr, salt_len, result_ptr, (UIntPtr)32);
                Marshal.Copy(result_ptr, hash, 0, 32);
                Marshal.FreeHGlobal(data_ptr);
                Marshal.FreeHGlobal(result_ptr);
                Marshal.FreeHGlobal(salt_ptr);
                return hash;
            }
            catch (Exception e)
            {
                Logging.error(string.Format("Error during mining: {0}", e.Message));
                return null;
            }
        }

        // Output the miner status
        private void printMinerStatus()
        {
            // Console.WriteLine("Miner: Block #{0} | Hashes per second: {1}", currentBlockNum, hashesPerSecond);
            lastStatTime = DateTime.UtcNow;
            lastHashRate = hashesPerSecond / 5;
            hashesPerSecond = 0;
        }

        // Returns the number of locally solved blocks
        public long getSolvedBlocksCount()
        {
            lock(solvedBlocks)
            {
                return solvedBlockCount;
            }
        }

        // Returns the number of empty and full blocks, based on PoW field
        public List<int> getBlocksCount()
        {
            int empty_blocks = 0;
            int full_blocks = 0;

            ulong lastBlockNum = Node.blockChain.getLastBlockNum();
            ulong oldestRedactedBlock = 0;
            if (lastBlockNum > ConsensusConfig.getRedactedWindowSize())
                oldestRedactedBlock = lastBlockNum - ConsensusConfig.getRedactedWindowSize();

            for (ulong i = lastBlockNum; i > oldestRedactedBlock; i--)
            {
                Block block = Node.blockChain.getBlock(i, false, false);
                if(block == null)
                {
                    continue;
                }
                if (block.powField == null)
                {
                    empty_blocks++;
                }
                else
                {
                    full_blocks++;
                }
            }
            List<int> result = new List<int>();
            result.Add(empty_blocks);
            result.Add(full_blocks);
            return result;
        }

        // Returns the relative time since the last block was solved
        public string getLastSolvedBlockRelativeTime()
        {
            if (lastSolvedTime == DateTime.MinValue)
                return "Never";

            return Clock.getRelativeTime(lastSolvedTime);
        }



        // Calculates the reward amount for a certain block
        public static IxiNumber calculateRewardForBlock(ulong blockNum)
        {
            ulong pow_reward = 0;

            if (blockNum < 1051200) // first year
            {
                pow_reward = (blockNum * 9) + 9; // +0.009 IXI
            }else if (blockNum < 2102400) // second year
            {
                pow_reward = (1051200 * 9);
            }else if (blockNum < 3153600) // third year
            {
                pow_reward = (1051200 * 9) + ((blockNum - 2102400) * 9) + 9; // +0.009 IXI
            }
            else if (blockNum < 4204800) // fourth year
            {
                pow_reward = (2102400 * 9) + ((blockNum - 3153600) * 2) + 2; // +0.0020 IXI
            }
            else if (blockNum < 5256001) // fifth year
            {
                pow_reward = (2102400 * 9) + (1051200 * 2) + ((blockNum - 4204800) * 9) + 9; // +0.009 IXI
            }
            else // after fifth year if mining is still operational
            {
                pow_reward = ((3153600 * 9) + (1051200 * 2))/2;
            }

            pow_reward = (pow_reward/2 + 10000) * 100000; // Divide by 2 (assuming 50% block coverage) + add inital 10 IXI block reward + add the full amount of 0s to cover IxiNumber decimals
            return new IxiNumber(new BigInteger(pow_reward)); // Generate the corresponding IxiNumber, including decimals
        }

        public void test()
        {
            while (1 == 1)
            {
                byte[] nonce = ASCIIEncoding.ASCII.GetBytes(ASCIIEncoding.ASCII.GetString(randomNonce(64)));
                byte[] hash = findHash_v1(new byte[3]{ 1, 2, 3 }, nonce);

                // We have a valid hash, update the corresponding block
                if (Miner.validateHashInternal_v1(hash, BitConverter.GetBytes(80)) == true)
                {
                    byte[] data = null;

                    using (MemoryStream mw = new MemoryStream())
                    {
                        using (BinaryWriter writerw = new BinaryWriter(mw))
                        {
                            string nonce_hex = ASCIIEncoding.ASCII.GetString(nonce);
                            writerw.Write(nonce_hex);
                            data = mw.ToArray();
                        }
                    }

                    string nonce_str = "";

                    // Extract the block number and nonce
                    using (MemoryStream m = new MemoryStream(data))
                    {
                        using (BinaryReader reader = new BinaryReader(m))
                        {
                            nonce_str = reader.ReadString();
                        }
                    }

                    byte[] nonce_bytes = ASCIIEncoding.ASCII.GetBytes(nonce_str);
                    byte[] hash_to_test = Miner.findHash_v1(new byte[3] { 1, 2, 3 }, nonce_bytes);

                    if (Miner.validateHashInternal_v1(hash_to_test, BitConverter.GetBytes(80)) == true)
                    {
                        // Hash is valid
                        Logging.error("Found correct PoW");
                        //break;
                    }else
                    {
                        Logging.error("PoW solution incorrect");
                    }
                }
            }
        }
    }
}
