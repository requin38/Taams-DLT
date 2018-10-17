﻿using DLT;
using DLT.Meta;
using DLT.Network;
using DLTNode;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace DLT
{
    class TransactionPool
    {
        // Estimate transaction pool memory size for ~3000 blocks @ ~3k tx per block
        // Current TX: ~200 B.
        //  3k transactions: ~600 KB
        //  3k blocks: 1.8 GB (Acceptable?)
        // Shrunken TX: 97B (See Transaction.cs)
        //  3k transactions: ~300 KB
        //  3k blocks: 850 MB
        //

        static readonly Dictionary<string, Transaction> transactions = new Dictionary<string, Transaction>();
        public static int activeTransactions
        {
            get
            {
                lock(transactions)
                {
                    return transactions.Count;
                }
            }
        }

        public static int numTransactions { get => activeTransactions; }

        public static int internalNonce = 0;  // Used to calculate the nonce when sending transactions from this node
                                                // TODO: move this to a more suitable location while still being able to reset it every block

        static TransactionPool()
        {
        }

        private TransactionPool()
        {
        }

        public static bool verifyTransaction(Transaction transaction)
        {
            ulong blocknum = Node.blockChain.getLastBlockNum();
            if (blocknum < 1)
            {
                if (transaction.type == (int)Transaction.Type.Genesis)
                {
                    // Adding GENESIS transaction
                    Logging.info("Received GENESIS transaction.");
                    return true;
                }
            }
            else
            if (blocknum < 10)
            {
                Logging.warn(String.Format("Ignoring transaction before block 10."));
                return false;
            }
            else if (transaction.type == (int)Transaction.Type.Genesis)
            {
                Logging.warn(String.Format("Genesis transaction on block #{0} skipped. TXid: {1}.", blocknum, transaction.id));
                return false;
            }

            // reject any transaction with block height 0, except for legacy transactions before new nonce
            if(blocknum > Legacy.up20181017 && transaction.blockHeight == 0)
            {
                Logging.warn(String.Format("Transaction without block height specified on block #{0} skipped. TXid: {1}.", blocknum, transaction.id));
                return false;
            }

            // Check the block height
            if (blocknum > Config.redactedWindowSize && transaction.blockHeight > Legacy.up20181017)
            {
                if (blocknum - Config.redactedWindowSize > transaction.blockHeight || transaction.blockHeight > blocknum + 1)
                {
                    Logging.warn(String.Format("Incorrect block height for transaction {0}. Tx nonce is {1}, expecting at least {2}", transaction.id, transaction.blockHeight, blocknum - Config.redactedWindowSize));
                    return false;
                }
            }

            // Prevent transaction spamming
            // Note: transactions that change multisig wallet parameters may have amount zero, since it will be ignored anyway
            if(transaction.type != (int)Transaction.Type.PoWSolution)
            if (transaction.amount == 0 && transaction.type != (int)Transaction.Type.ChangeMultisigWallet)
            {
                return false;
            }

            // Run through existing transactions in the pool and verify for double-spending / invalid states
            // Note that we lock the transaction for the entire duration of the checks, which might pose performance issues
            // Todo: find a better way to handle this without running into threading bugs
            // Lock transactions to prevent threading bugs
            lock (transactions)
            {
                // Search for dups
                if (transactions.ContainsKey(transaction.id) && transaction.type != (int)Transaction.Type.MultisigTX)
                {
                    // we already have this transaction, discard
                    return false;
                }
            }

            // Prevent sending to the sender's address
            // unless it's a multisig change transaction
            if (transaction.type != (int)Transaction.Type.ChangeMultisigWallet && transaction.from.Equals(transaction.to, StringComparison.Ordinal))
            {
                Logging.warn(string.Format("Invalid TO address for transaction id: {0}", transaction.id));
                return false;
            }

            // Calculate the transaction checksum and compare it
            string checksum = Transaction.calculateChecksum(transaction);
            if (checksum.Equals(transaction.checksum) == false)
            {
                Logging.warn(String.Format("Adding transaction {{ {0} }}, but checksum doesn't match!", transaction.id));
                return false;
            }

            if (!Address.validateChecksum(transaction.from))
            {
                Logging.warn(String.Format("Adding transaction {{ {0} }}, but from address is incorrect!", transaction.id));
                return false;
            }

            if (!Address.validateChecksum(transaction.to))
            {
                Logging.warn(String.Format("Adding transaction {{ {0} }}, but to address is incorrect!", transaction.id));
                return false;
            }
            

            // Special case for PoWSolution transactions
            if (transaction.type == (int)Transaction.Type.PoWSolution)
            {
                // TODO: pre-validate the transaction in such a way it doesn't affect performance
                //Logging.warn("Denied mining transaction before mining is enabled.");
                //return false;
                Logging.error(string.Format("RECEIVED MINING TX: {0}", transaction.id));
            }
            // Special case for Staking Reward transaction
            else if (transaction.type == (int)Transaction.Type.StakingReward)
            {

            }
            // Special case for Genesis transaction
            else if (transaction.type == (int)Transaction.Type.Genesis)
            {
                // Ignore if it's not in the genesis block
                if (blocknum > 1)
                {
                    Logging.warn(String.Format("Genesis transaction on block #{0} ignored. TXid: {1}.", blocknum, transaction.id));
                    return false;
                }
            }
            else
            {
                // Verify if the transaction contains the minimum fee
                if (transaction.fee < Config.transactionPrice)
                {
                    // Prevent transactions that can't pay the minimum fee
                    Logging.warn(String.Format("Transaction fee does not cover minimum fee for {{ {0} }}.", transaction.id));
                    return false;
                }

                if (Node.blockSync.synchronizing == false)
                {
                    // Verify the transaction against the wallet state
                    // If the balance after the transaction is negative, do not add it.
                    IxiNumber fromBalance = Node.walletState.getWalletBalance(transaction.from);
                    IxiNumber finalFromBalance = fromBalance - transaction.amount - transaction.fee;

                    if (finalFromBalance < (long)0)
                    {
                        // Prevent overspending
                        Logging.warn(String.Format("Attempted to overspend with transaction {{ {0} }}.", transaction.id));
                        return false;
                    }
                }
            }
            /*var sw = new System.Diagnostics.Stopwatch();
            sw.Start();*/

            // Extract the public key if found. Used for transaction verification.
            // TODO: check pubkey walletstate support
            string pubkey = "";

            if (transaction.type == (int)Transaction.Type.Genesis ||
                transaction.type == (int)Transaction.Type.StakingReward)
            {
                if (transaction.type == (int)Transaction.Type.StakingReward)
                    pubkey = Node.walletStorage.publicKey;
            }
            else
            {
                pubkey = Node.walletState.getWallet(transaction.from).publicKey;
                // Generate an address from the public key and compare it with the sender
                if (pubkey.Length < 1)
                {
                    // If this is a PoWSolution transaction, extract the public key from the data section first
                    if (transaction.type == (int)Transaction.Type.PoWSolution)
                    {
                        string[] split = transaction.data.Split(new string[] { "||" }, StringSplitOptions.None);
                        if (split.Length < 1)
                            return false;
                        pubkey = split[0];
                    }
                    else
                    {
                        // There is no supplied public key, extract it from the data section
                        pubkey = transaction.data;
                    }
                }
            }

            // Finally, verify the signature
            if (transaction.type == (int)Transaction.Type.MultisigTX || transaction.type == (int)Transaction.Type.ChangeMultisigWallet)
            {
                Logging.info(String.Format("Transaction {{ {0} }} is a multisig transaction.", transaction.id));
                Wallet orig = Node.walletState.getWallet(transaction.from);
                if (orig.type == WalletType.Multisig)
                {
                    string[] signatures = transaction.signature.Split(',');
                    Logging.info(String.Format("Multisig transaction has {0} signatures, {1} required.", signatures.Length, orig.requiredSigs));
                    if (numValidMultisigs(transaction.checksum, signatures, orig) < 0)
                    {
                        Logging.warn(String.Format("Transaction {{ {0} }}: has invalid multisigs for wallet {1}.",
                            transaction.id, orig.id));
                        return false;
                    }
                } else //! WalletType.Multisig
                {
                    if (transaction.type == (int)Transaction.Type.ChangeMultisigWallet)
                    {
                        string[] tx_data_fields = transaction.data != null ? transaction.data.Split(':') : new string[0];
                        if (tx_data_fields.Length < 2)
                        {
                            Logging.warn(String.Format("Transaction {{ {0} }} is a change multisig transaction but the data field does not contain appropriate data.", transaction.id));
                            return false;
                        }
                        if (tx_data_fields[0] != "MS1")
                        {
                            Logging.warn(String.Format("Transaction {{ {0} }} is a change multisig transaction on a non-multisig wallet. Only type 'MS1' is allowed, but the transaction has type {1}.",
                                transaction.id, tx_data_fields[0]));
                            return false;
                        }
                        // ChangeMultisigWallet Transaction type with subtype 'MS1' is allowed to operate on a normal wallet,
                        // because it will change the wallet to a multisig wallet.
                        return true;
                    }
                    Logging.warn(String.Format("Transaction {{ {0} }} is a multisig transaction, but source wallet {1} is not multisig wallet!",
                        transaction.id, transaction.from));
                    return false;
                }
            } else if (transaction.verifySignature(pubkey) == false)
            {
                // Transaction signature is invalid
                Logging.warn(string.Format("Invalid signature for transaction id: {0}", transaction.id));
                return false;
            }
            /*sw.Stop();
            TimeSpan elapsed = sw.Elapsed;
            Logging.info(string.Format("VerifySignature duration: {0}ms", elapsed.TotalMilliseconds));*/

            return true;
        }

        public static bool setAppliedFlag(string txid, ulong blockNum)
        {
            Transaction t;
            lock (transactions)
            {
                if (!transactions.ContainsKey(txid))
                {
                    return false;
                }
                t = transactions[txid];
                t.applied = blockNum;
            }
            // Storage the transaction in the database
            Meta.Storage.insertTransaction(t);
            return true;
        }

        // Adds a non-applied transaction to the memory pool
        // Returns true if the transaction is added to the pool, false otherwise
        public static bool addTransaction(Transaction transaction, bool no_broadcast = false, RemoteEndpoint skipEndpoint = null)
        {
            if (Node.blockSync.synchronizing == false)
            {
                if (!verifyTransaction(transaction))
                {
                    return false;
                }
            }
            
            //Logging.info(String.Format("Accepted transaction {{ {0} }}, amount: {1}", transaction.id, transaction.amount));

            // Lock transactions to prevent threading bugs
            lock (transactions)
            {
                // Search for dups again

                if (transactions.ContainsKey(transaction.id))
                {
                    if (transaction.type == (int)Transaction.Type.MultisigTX || transaction.type == (int)Transaction.Type.ChangeMultisigWallet)
                    {
                        Logging.info(String.Format("Appending a multisig transaction {{ {0} }} in the pool.", transaction.id));
                        Transaction pool_tx = transactions[transaction.id];
                        if (pool_tx.type != (int)Transaction.Type.MultisigTX)
                        {
                            Logging.error(String.Format("Attempted to add a multisig transaction to override a normal transaction: {{ {0} }}.",
                                transaction.id));
                            return false;
                        }
                        HashSet<string> final_sigs = new HashSet<string>(pool_tx.signature.Split(','));
                        string[] new_sigs = transaction.signature.Split(',');
                        bool increased_sig_count = false;
                        foreach (string new_sig in new_sigs)
                        {
                            if (final_sigs.Add(new_sig))
                            {
                                increased_sig_count = true;
                            }
                        }
                        if (increased_sig_count)
                        {
                            Logging.info(String.Format("More signatures acquired for a multisig transaction {{ {0} }}: {1}.",
                                transaction.id, final_sigs.Count));
                            pool_tx.signature = string.Join(",", final_sigs);
                        }
                    }
                }
                else
                {
                    transactions.Add(transaction.id, transaction);
                }
            }

            //   Logging.info(String.Format("Transaction {{ {0} }} has been added.", transaction.id, transaction.amount));
            //Console.WriteLine("Transaction {{ {0} }} has been added.", transaction.id, transaction.amount);
            Console.Write("$");

            if (Node.blockSync.synchronizing == true)
            {
                return true;
            }

            // Broadcast this transaction to the network
            if (no_broadcast == false)
                ProtocolMessage.broadcastProtocolMessage(ProtocolMessageCode.newTransaction, transaction.getBytes(), skipEndpoint);


            return true;
        }

        // Attempts to retrieve a transaction from memory or from storage
        // Returns null if no transaction is found
        public static Transaction getTransaction(string txid, bool search_in_storage = false)
        {
            Transaction transaction = null;

            lock(transactions)
            {
                //Logging.info(String.Format("Looking for transaction {{ {0} }}. Pool has {1}.", txid, transactions.Count));
                transaction = transactions.ContainsKey(txid) ? transactions[txid] : null;
            }

            if (transaction != null)
                return transaction;

            if (search_in_storage)
            {
                // No transaction found in memory, look into storage

                /*var sw = new System.Diagnostics.Stopwatch();
                sw.Start();*/
                transaction = Storage.getTransaction(txid);
                /*sw.Stop();
                TimeSpan elapsed = sw.Elapsed;
                Logging.info(string.Format("StopWatch duration: {0}ms", elapsed.TotalMilliseconds));*/
            }

            return transaction;
        }

        // Removes all transactions from TransactionPool linked to a block.
        public static bool redactTransactionsForBlock(Block block)
        {
            if (block == null)
                return false;

            lock (transactions)
            {
                foreach (string txid in block.transactions)
                {
                    transactions.Remove(txid);
                }
            }
            return true;
        }

        public static Transaction[] getAllTransactions()
        {
            lock(transactions)
            {
                return transactions.Select(e => e.Value).ToArray();
            }
        }

        public static Transaction[] getUnappliedTransactions()
        {
            lock(transactions)
            {
                return transactions.Select(e => e.Value).Where(x => x.applied == 0).ToArray();
            }
        }

        // This updates a pre-existing transaction
        // Returns true if the transaction has been updated, false otherwise
        // TODO TODO TODO we'll run into problems with this because of the new txid, needs to be done differently, commenting this function out for now
        /*
        public static bool updateTransaction(Transaction transaction)
        {
            Logging.info(String.Format("Received transaction {0} - {1} - {2}.", transaction.id, transaction.checksum, transaction.amount));

            if (!verifyTransaction(transaction))
            {
                return false;
            }

            // Run through existing transactions in the pool and verify for double-spending / invalid states
            // Note that we lock the transaction for the entire duration of the checks, which might pose performance issues
            // Todo: find a better way to handle this without running into threading bugs
            lock (transactions)
            {
                foreach (Transaction tx in transactions)
                {
                    if (tx.id.Equals(transaction.id, StringComparison.Ordinal) == true)
                    {
                        if (tx.applied == 0)
                        {
                            tx.amount = transaction.amount;
                            tx.data = transaction.data;
                            tx.nonce = transaction.nonce
                            tx.checksum = transaction.checksum;

                            // Broadcast this transaction update to the network
                            //ProtocolMessage.broadcastProtocolMessage(ProtocolMessageCode.updateTransaction, transaction.getBytes());

                            // Also update the transaction to storage
                            Meta.Storage.insertTransaction(transaction);

                            Logging.info(String.Format("Updated transaction {0} - {1} - {2}.", transaction.id, transaction.checksum, transaction.amount));

                            return true;
                        }
                        else
                        {
                            Logging.info(String.Format("Transaction was already applied, not updating {0} - {1} - {2}.", transaction.id, transaction.checksum, transaction.amount));
                        }
                    }
                }

            }

            return false;
        }*/

        // Verify if a PoW transaction is valid
        public static bool verifyPoWTransaction(Transaction tx, out ulong blocknum)
        {
            blocknum = 0;
            if (tx.type != (int)Transaction.Type.PoWSolution)
                return false;

            // Split the transaction data field
            string[] split = tx.data.Split(new string[] { "||" }, StringSplitOptions.None);
            if (split.Length < 3)
                return false;
            try
            {
                // Extract the block number and nonce
                ulong block_num = Convert.ToUInt64(split[1]);
                blocknum = block_num;
                string nonce = split[2];

                // Check if the block has an empty PoW field
                Block block = Node.blockChain.getBlock(block_num);
                if(block.powField.Length > 0)
                {
                    Logging.error("POW already applied");
                    return false;
                }

                // Verify the nonce
                if (Miner.verifyNonce(nonce, block_num, tx.from, block.difficulty))
                {
                    return true;
                }
            }
            catch(Exception e)
            {
                Logging.warn(string.Format("Error verifying PoW Transaction: {0}. Message: {1}", tx.id, e.Message));
            }

            return false;
        }

        // TODO TODO TODO This is a very dangerous function, replace as soon as possible
        public static bool setAppliedFlagToTransactionsFromBlock(Block b)
        {
            if (b == null)
            {
                return true;
            }
            lock (transactions)
            {
                foreach (string txid in b.transactions)
                {
                    Transaction tx = getTransaction(txid);
                    if (tx == null)
                    {
                        Logging.error(String.Format("Attempted to set applied to transaction from block #{0} ({1}), but transaction {{ {2} }} was missing.",
                            b.blockNum, b.blockChecksum, txid));
                        return false;
                    }
                    setAppliedFlag(txid, b.blockNum);
                }
            }
            return true;
        }

        public static void applyStakingTransactionsFromBlock(Block block, List<Transaction> failed_staking_transactions, bool ws_snapshot = false)
        {
            // TODO: move this to a seperate function. Left here for now for dev purposes
            // Apply any staking transactions in the pool at this moment
            List<Transaction> staking_txs = null;
            if (ws_snapshot)
            {
                staking_txs = Node.blockProcessor.generateStakingTransactions(block.blockNum - 6, ws_snapshot);
            }
            else
            {
                lock (transactions)
                {
                    staking_txs = transactions.Select(e => e.Value).Where(x => x.type == (int)Transaction.Type.StakingReward).ToList();
                }
            }

            // Maintain a list of stakers
            List<string> blockStakers = new List<string>();

            foreach (Transaction tx in staking_txs)
            {
                if (tx.applied > 0)
                    continue;
              
                string[] split_str = tx.id.Split(new string[] { "-" }, StringSplitOptions.None);
                ulong txbnum = Convert.ToUInt64(split_str[1]);

                if (txbnum != block.blockNum - 6)
                    continue;

                // Special case for Staking Reward transaction
                // Do not apply them if we are synchronizing
                // TODO: note that this can backfire when recovering completely from a file
                if (Node.blockSync.synchronizing && Config.recoverFromFile == false)
                    continue;
               
                if (applyStakingTransaction(tx, block, failed_staking_transactions, blockStakers, ws_snapshot))
                {
                    //Console.WriteLine("!!! APPLIED STAKE {0}", tx.id);
                    continue;
                }
            }

        }

        // This applies all the transactions from a block to the actual walletstate.
        // It removes the failed transactions as well from the pool and block.
        public static bool applyTransactionsFromBlock(Block block, bool ws_snapshot = false)
        {
            if (block == null)
            {
                return true;
            }

            try
            {
                // Maintain a dictionary of block solutions and the corresponding miners for solved blocks
                IDictionary<ulong, List<string>> blockSolutionsDictionary = new Dictionary<ulong, List<string>>();

                // Maintain a list of failed transactions to remove them from the TxPool in one go
                List<Transaction> failed_transactions = new List<Transaction>();
                List<Transaction> already_applied_transactions = new List<Transaction>();

                List<Transaction> failed_staking_transactions = new List<Transaction>();

                applyStakingTransactionsFromBlock(block, failed_staking_transactions, ws_snapshot);

                // Remove all failed transactions from the TxPool
                foreach (Transaction tx in failed_staking_transactions)
                {
                    Logging.warn(String.Format("Removing failed staking transaction #{0} from pool.", tx.id));
                    if (tx.applied == 0)
                    {
                        lock (transactions)
                        {
                            // Remove from TxPool
                            transactions.Remove(tx.id);
                        }
                    }
                    else
                    {
                        Logging.error(String.Format("Error, attempting to remove failed transaction #{0} from pool, that was already applied.", tx.id));
                    }
                    //block.transactions.Remove(tx.id);
                }
                if (failed_staking_transactions.Count > 0)
                {
                    failed_staking_transactions.Clear();
                    Logging.error(string.Format("Block #{0} has failed staking transactions, rejecting the block.", block.blockNum));
                    return false;
                }


                foreach (string txid in block.transactions)
                {
                    // Skip staking txids
                    if (txid.StartsWith("stk"))
                    {
                        if (Node.blockSync.synchronizing)
                        {
                            if (getTransaction(txid) == null)
                            {
                                Logging.info(string.Format("Missing staking transaction during sync: {0}", txid));
                            }
                        }
                        continue;
                    }

                    Transaction tx = getTransaction(txid);

                    if (tx == null)
                    {
                        Logging.error(String.Format("Attempted to apply transactions from block #{0} ({1}), but transaction {{ {2} }} was missing.",
                            block.blockNum, block.blockChecksum, txid));
                        return false;
                    }

                    if (tx.type == (int)Transaction.Type.StakingReward)
                    {
                        continue;
                    }

                    // TODO TODO TODO needs additional checking if it's really applied in the block it says it is; this is a potential for exploit, where a malicious node would send valid transactions that would get rejected by other nodes
                    if (tx.applied > 0 && tx.applied != block.blockNum)
                    {
                        // remove transaction from block as it has already been applied on a different block
                        already_applied_transactions.Add(tx);
                        continue;
                    }

                    // Special case for PoWSolution transactions
                    if (applyPowTransaction(tx, block, blockSolutionsDictionary, ws_snapshot))
                    {
                        continue;
                    }

                    // Check the transaction amount
                    if (tx.amount == 0 && tx.type != (int)Transaction.Type.ChangeMultisigWallet)
                    {
                        failed_transactions.Add(tx);
                        continue;
                    }

                    // Special case for Genesis transactions
                    if (applyGenesisTransaction(tx, block, failed_transactions, ws_snapshot))
                    {
                        continue;
                    }

                    // Update the walletstate public key
                    // TODO: check pubkey walletstate support
                    string pubkey = Node.walletState.getWallet(tx.from, ws_snapshot).publicKey;
                    // Generate an address from the public key and compare it with the sender
                    if (pubkey.Length < 1)
                    {
                        // There is no supplied public key, extract it from the data section
                        pubkey = tx.data;

                        // Update the walletstate public key
                        Node.walletState.setWalletPublicKey(tx.from, pubkey, ws_snapshot);
                    }


                    // Special case for Multisig
                    if (tx.type == (int)Transaction.Type.MultisigTX)
                    {
                        applyMultisigTransaction(tx, block, failed_transactions, ws_snapshot);
                        continue;
                    }
                    if(tx.type == (int)Transaction.Type.ChangeMultisigWallet)
                    {
                        applyMultisigChangeTransaction(tx, block, failed_transactions, ws_snapshot);
                        continue;
                    }

                    // If we reached this point, it means this is a normal transaction
                    applyNormalTransaction(tx, block, failed_transactions, ws_snapshot);

                }

                // Finally, Check if we have any miners to reward
                if (blockSolutionsDictionary.Count > 0)
                {
                    rewardMiners(blockSolutionsDictionary, ws_snapshot);
                }

                // Clear the solutions dictionary
                blockSolutionsDictionary.Clear();

                // Remove all failed transactions from the TxPool and block
                foreach (Transaction tx in failed_transactions)
                {
                    Logging.warn(String.Format("Removing failed transaction #{0} from pool.", tx.id));
                    // Remove from TxPool
                    if (tx.applied == 0)
                    {
                        lock (transactions)
                        {
                            transactions.Remove(tx.id);
                        }
                    }
                    else
                    {
                        Logging.error(String.Format("Error, attempting to remove failed transaction #{0} from pool, that was already applied.", tx.id));
                    }
                    //block.transactions.Remove(tx.id);
                }
                if (failed_transactions.Count > 0)
                {
                    failed_transactions.Clear();
                    Logging.error(string.Format("Block #{0} has failed transactions, rejecting the block.", block.blockNum));
                    return false;
                }

                // Remove all already applied transactions from the block
                /*foreach (Transaction tx in already_applied_transactions)
                {
                    Logging.info(String.Format("Removing already applied transaction #{0} from block.", tx.id));
                    block.transactions.Remove(tx.id);
                }
                already_applied_transactions.Clear();*/

                if (already_applied_transactions.Count > 0)
                {
                    already_applied_transactions.Clear();
                    Logging.error(string.Format("Block #{0} has transactions that were already applied on other blocks, rejecting the block.", block.blockNum));
                    return false;
                }
            }
            catch (Exception e)
            {
                Logging.error(string.Format("Error applying transactions from block #{0}. Message: {1}", block.blockNum, e.Message));
                return false;
            }
            
            return true;
        }

        // Checks if a transaction is a pow transaction and applies it.
        // Returns true if it's a PoW transaction, otherwise false
        public static bool applyPowTransaction(Transaction tx, Block block, IDictionary<ulong, List<string>> blockSolutionsDictionary, bool ws_snapshot = false)
        {
            if (tx.type != (int)Transaction.Type.PoWSolution)
            {
                return false;
            }
            //   Logging.warn("Trying to apply PoW transaction before mining is enabled.");
            //   return false;
            Logging.error(string.Format("Applying POW: {0}", tx.id));
            // Update the block's applied field
            if (!ws_snapshot)
            {
                setAppliedFlag(tx.id, block.blockNum);
            }

            // Verify if the solution is correct
            if (verifyPoWTransaction(tx, out ulong powBlockNum) == true)
            {
                // Check if we already have a key matching the block number
                if (blockSolutionsDictionary.ContainsKey(powBlockNum) == false)
                {
                    blockSolutionsDictionary[powBlockNum] = new List<string>();
                }
                // Add the miner to the block number dictionary reward list
                blockSolutionsDictionary[powBlockNum].Add(tx.from);
            }

            return true;
        }

        // Checks if a transaction is a genesis transaction and applies it.
        // Returns true if it's a PoW transaction, otherwise false
        public static bool applyGenesisTransaction(Transaction tx, Block block, List<Transaction> failed_transactions, bool ws_snapshot = false)
        {
            if (tx.type != (int)Transaction.Type.Genesis)
            {
                return false;
            }

            // Check for the genesis block first
            if (block.blockNum > 1)
            {
                // Add it to the failed transactions list
                Logging.error(String.Format("Genesis transaction {0} detected after block #1. Ignored.", tx.id));
                failed_transactions.Add(tx);
                return true;
            }

            // Apply the amount
            Node.walletState.setWalletBalance(tx.to, tx.amount, ws_snapshot);
            if (!ws_snapshot)
            {
                setAppliedFlag(tx.id, block.blockNum);
            }

            return true;
        }

        // Checks if a transaction is a staking transaction and applies it.
        // Returns true if it's a Staking transaction, otherwise false
        public static bool applyStakingTransaction(Transaction tx, Block block, List<Transaction> failed_transactions, List<string> blockStakers, bool ws_snapshot = false)
        {
            if (tx.type != (int)Transaction.Type.StakingReward)
            {
                return false;
            }

            // Check if the staker's transaction has already been processed
            bool valid = true;
            foreach (string staker in blockStakers)
            {
                if (staker.Equals(tx.to, StringComparison.Ordinal))
                {
                    valid = false;
                    break;
                }
            }
            
            // If there's another staking transaction for the staker in this block, ignore
            if (valid == false)
            {
                Logging.error(String.Format("There's a duplicate staker transaction {0}.", tx.id));
                failed_transactions.Add(tx);
                return true;
            }

            Wallet staking_wallet = Node.walletState.getWallet(tx.to, ws_snapshot);
            IxiNumber staking_balance_before = staking_wallet.balance;

            IxiNumber tx_amount = tx.amount;

            if (tx_amount < new IxiNumber(1))
            {
                Logging.error(String.Format("Staking transaction {0} does not have a positive amount.", tx.id));
                failed_transactions.Add(tx);
                return true;
            }

            // Check if the transaction is in the sigfreeze
            // TODO: refactor this and make it more efficient
            string blocknum = tx.data;
            // Verify the staking transaction is accurate
            Block targetBlock = Node.blockChain.getBlock(Convert.ToUInt64(blocknum));
            if (targetBlock == null)
            {
                failed_transactions.Add(tx);
                return true;
            }
            
            valid = false;
            List<string> signatureWallets = targetBlock.getSignaturesWalletAddresses();
            foreach (string wallet_addr in signatureWallets)
            {
                if (tx.to.Equals(wallet_addr))
                    valid = true;
            }
            if (valid == false)
            {
                Logging.error(String.Format("Staking transaction {0} does not have a corresponding block signature.", tx.id));
                failed_transactions.Add(tx);
                return true;
            }

            // Deposit the amount
            IxiNumber staking_balance_after = staking_balance_before + tx_amount;

            Node.walletState.setWalletBalance(tx.to, staking_balance_after, ws_snapshot, staking_wallet.nonce);
            if (!ws_snapshot)
            {
                setAppliedFlag(tx.id, block.blockNum);
            }

            blockStakers.Add(tx.to);
            
            return true;
        }

        // Rolls back a normal transaction
        public static bool rollBackNormalTransaction(Transaction tx)
        {
            // Calculate the transaction amount without fee
            IxiNumber txAmountWithoutFee = tx.amount - Config.transactionPrice;

            Wallet source_wallet = Node.walletState.getWallet(tx.from);
            Wallet dest_wallet = Node.walletState.getWallet(tx.to);

            IxiNumber source_balance_before = source_wallet.balance;
            IxiNumber dest_balance_before = dest_wallet.balance;

            // Withdraw the full amount, including fee
            IxiNumber source_balance_after = source_balance_before + tx.amount + tx.fee;

            // Deposit the amount without fee, as the fee is distributed by the network a few blocks later
            IxiNumber dest_balance_after = dest_balance_before - tx.amount;

            // Update the walletstate
            Node.walletState.setWalletBalance(tx.from, source_balance_after, false, source_wallet.nonce);
            Node.walletState.setWalletBalance(tx.to, dest_balance_after, false, dest_wallet.nonce);

            return true;
        }

        public static int numValidMultisigs(string checksum, string[] signatures, Wallet w)
        {
            // signatures are "pubkey:signature"
            HashSet<string> pubkeys = new HashSet<string>();
            // they must all be valid signatures for the checksum
            foreach (string sig_c in signatures)
            {
                string[] sig_parts = sig_c.Split(':');
                if (sig_parts.Length < 2)
                {
                    return -1;
                }
                pubkeys.Add(sig_parts[0]);
                if (!CryptoManager.lib.verifySignature(checksum, sig_parts[0], sig_parts[1]))
                {
                    return -1;
                }
            }
            // how many of them are on the wallet's allowed list?
            return w.matchValidSigners(pubkeys.ToArray());
        }

        public static bool applyMultisigTransaction(Transaction tx, Block block, List<Transaction> failed_transactions, bool ws_snapshot = false)
        {
            if(tx.type == (int)Transaction.Type.MultisigTX)
            {
                string[] sigs = tx.signature.Split(',');
                Wallet orig = Node.walletState.getWallet(tx.from, ws_snapshot);
                if(orig.type != WalletType.Multisig)
                {
                    Logging.error(String.Format("Attempted to apply a multisig TX where the originating wallet is not a multisig wallet! Wallet: {0}, Transaction: {{ {1} }}.",
                        tx.from, tx.id));
                    failed_transactions.Add(tx);
                    return false;
                }
                if(sigs.Length >= orig.requiredSigs)
                {
                    // candidate for processing
                    int num_valid_sigs = numValidMultisigs(tx.checksum, sigs, orig);
                    if(num_valid_sigs < 1)
                    {
                        Logging.error(String.Format("Transaction {{ {0} }} (multisig) has invalid signatures!", tx.id));
                        failed_transactions.Add(tx);
                        return false;
                    }
                    if(num_valid_sigs < orig.requiredSigs)
                    {
                        Logging.info(String.Format("Transaction {{ {0} }} (multisig) has {1} out of {2} required sigs.",
                            tx.id, num_valid_sigs, orig.requiredSigs));
                        return true;
                    }
                    return applyNormalTransaction(tx, block, failed_transactions, ws_snapshot);
                }
                // ignore if it doesn't have enough sigs - it will be pruned from the TXPool after a while
            }
            return false;
        }

        public static bool applyMultisigChangeTransaction(Transaction tx, Block block, List<Transaction> failed_transactions, bool ws_snapshot = false)
        {
            if(tx.type == (int)Transaction.Type.ChangeMultisigWallet)
            {
                string[] sigs = tx.signature.Split(',');
                Wallet orig = Node.walletState.getWallet(tx.from, ws_snapshot);
                string[] tx_data_fields = tx.data != null ? tx.data.Split(':') : new string[0];
                if(tx_data_fields.Length < 2)
                {
                    Logging.warn(String.Format("Transaction {{ {0} }}: Improperly formatted ChangeMultisig transaction data field: {1}. Should contain a MS_ segment.", tx.id, tx.data));
                    failed_transactions.Add(tx);
                    return false;
                }
                Transaction.MultisigWalletChangeType change_type = Transaction.MultisigWalletChangeType.AddSigner;
                switch(tx_data_fields[0])
                {
                    case "MS1": change_type = Transaction.MultisigWalletChangeType.AddSigner; break;
                    case "MS2": change_type = Transaction.MultisigWalletChangeType.DelSigner; break;
                    case "MS3":change_type = Transaction.MultisigWalletChangeType.ChangeReqSigs; break;
                    default:
                        {
                            Logging.error(String.Format("Transaction {{ {0} }}: Invalid ChangeMultisig value: {1}.", tx.id, tx_data_fields[0]));
                            failed_transactions.Add(tx);
                            return false;
                        }
                }
                if(orig.type != WalletType.Multisig && change_type != Transaction.MultisigWalletChangeType.AddSigner)
                {
                    Logging.error(String.Format("Transaction {{ {0} }}: Target wallet ({1}) is not a multisig wallet and the transaction will not create one!", tx.id, orig.id));
                    failed_transactions.Add(tx);
                    return false;
                }
                int num_valid_sigs = numValidMultisigs(tx.checksum, sigs, orig);
                if (num_valid_sigs < 0)
                {
                    Logging.info(String.Format("Transaction {{ {0} }} has invalid multisig signatures!", tx.id));
                    failed_transactions.Add(tx);
                    return false;
                }
                if (num_valid_sigs < orig.requiredSigs)
                {
                    Logging.info(String.Format("Transaction {{ {0} }} has {1} valid signatures out of required {2}.", tx.id, num_valid_sigs, orig.requiredSigs));
                    return true;
                }
                // if we came this far, all checks pass
                Logging.info(String.Format("Applying a change multisig wallet transaction: {{ {0} }}, of type {1} to wallet {2}. The data is: {3}. Snapshot = {4}",
                    tx.id, change_type.ToString(), orig.id, tx_data_fields[1], ws_snapshot));
                if(ws_snapshot == false)
                {
                    ws_snapshot = false;
                }
                // todo: there is no verification that the third data field really contains a pubkey
                switch(change_type)
                {
                    case Transaction.MultisigWalletChangeType.AddSigner:
                        string sig_to_add = tx_data_fields[1];
                        // TODO this would be a place to verify if the address is valid
                        HashSet<string> current_pubkeys = new HashSet<string>();
                        if(orig.allowedSigners != null)
                        {
                            foreach (string signer in orig.allowedSigners) current_pubkeys.Add(signer);
                        } else
                        {
                            current_pubkeys.Add(orig.id);
                        }
                        current_pubkeys.Add(sig_to_add);
                        Logging.info(String.Format("Adding multisig address {0} to wallet {1}.", sig_to_add, orig.id));
                        orig.allowedSigners = current_pubkeys.ToArray();
                        orig.type = WalletType.Multisig;
                        Node.walletState.setWallet(orig, ws_snapshot);
                        break;
                    case Transaction.MultisigWalletChangeType.DelSigner:
                        string sig_to_del = tx_data_fields[1];
                        // TODO this would be a place to verify if the address is valid
                        current_pubkeys = new HashSet<string>(orig.allowedSigners);
                        Logging.info(String.Format("Removing multisig address {0} to wallet {1}.", sig_to_del, orig.id));
                        if (!current_pubkeys.Remove(sig_to_del))
                        {
                            Logging.warn(String.Format("Attempted to remove multisig address {0} from wallet {1}, but the key is not in the wallet's list. Ignoring.", sig_to_del, orig.id));
                        }
                        if(sig_to_del == orig.id)
                        {
                            Logging.error(String.Format("Attempted to remove wallet owner ({0}) from the multisig wallet!", sig_to_del));
                            failed_transactions.Add(tx);
                            return false;
                        }
                        orig.allowedSigners = current_pubkeys.ToArray();
                        if(orig.requiredSigs > orig.allowedSigners.Length)
                        {
                            Logging.info(String.Format("Removing a signer would make using the wallet impossible. Adjusting required signatures: {0} -> {1}.",
                                orig.requiredSigs, orig.allowedSigners.Length));
                            orig.requiredSigs = (byte)orig.allowedSigners.Length;
                        }
                        Node.walletState.setWallet(orig, ws_snapshot);
                        break;
                    case Transaction.MultisigWalletChangeType.ChangeReqSigs:
                        if(byte.TryParse(tx_data_fields[1], out byte reqSigs))
                        {
                            Logging.info(String.Format("Changing multisig wallet {0} required sigs {1} -> {2}.", orig.id, orig.requiredSigs, reqSigs));
                            orig.requiredSigs = reqSigs;
                            if(orig.requiredSigs == 1)
                            {
                                Logging.info(String.Format("Wallet {0} changes back to a single-sig wallet.", orig.id));
                                orig.type = WalletType.Normal;
                                orig.allowedSigners = null;
                            }
                            if(orig.requiredSigs > orig.allowedSigners.Length)
                            {
                                Logging.warn(String.Format("Attempted to set required sigs for a multisig wallet to a larger value than the number of allowed pubkeys! Pubkeys = {0}, reqSigs = {1}. Ignoring.",
                                    orig.allowedSigners.Length, reqSigs));
                            }
                            Node.walletState.setWallet(orig, ws_snapshot);
                        } else
                        {
                            Logging.warn(String.Format("Attempted to change a multisig wallet {0} required sigs to an invalid value '{1}'.", orig.id, tx_data_fields[1]));
                        }
                        break;
                    default: break; // this eventually will never happen
                }
                if (!ws_snapshot)
                {
                    setAppliedFlag(tx.id, block.blockNum);
                }
                return true;

            }
            return false;
        }


        // Applies a normal transaction
        public static bool applyNormalTransaction(Transaction tx, Block block, List<Transaction> failed_transactions, bool ws_snapshot = false)
        {
            // Calculate the transaction amount without fee
            IxiNumber txAmountWithoutFee = tx.amount - Config.transactionPrice;

            // Check if the fee covers the current network minimum fee
            // TODO: adjust this dynamically

            if(tx.fee - Config.transactionPrice < (long)0)
            {
                Logging.error(String.Format("Transaction {{ {0} }} cannot pay minimum fee", tx.id));
                failed_transactions.Add(tx);
                return false;
            }

            Wallet source_wallet = Node.walletState.getWallet(tx.from, ws_snapshot);
            Wallet dest_wallet = Node.walletState.getWallet(tx.to, ws_snapshot);

            IxiNumber source_balance_before = source_wallet.balance;
            IxiNumber dest_balance_before = dest_wallet.balance;

            // Withdraw the full amount, including fee
            IxiNumber source_balance_after = source_balance_before - tx.amount - tx.fee;
            if (source_balance_after < (long)0)
            {
                Logging.warn(String.Format("Transaction {{ {0} }} in block #{1} ({2}) would take wallet {3} below zero.",
                    tx.id, block.blockNum, block.lastBlockChecksum, tx.from));
                failed_transactions.Add(tx);
                return false;
            }

            // Check the block height
            if (block.blockNum > Config.redactedWindowSize && tx.blockHeight > Legacy.up20181017)
            {
                if (block.blockNum - Config.redactedWindowSize > tx.blockHeight || tx.blockHeight > block.blockNum)
                {
                    Logging.warn(String.Format("Incorrect block height for transaction {0}. Tx nonce is {1}, expecting at least {2}", tx.id, tx.blockHeight, block.blockNum - Config.redactedWindowSize));
                    failed_transactions.Add(tx);
                    return false;
                }
            }


            // Deposit the amount without fee, as the fee is distributed by the network a few blocks later
            IxiNumber dest_balance_after = dest_balance_before + tx.amount;


            // Update the walletstate
            Node.walletState.setWalletBalance(tx.from, source_balance_after, ws_snapshot, source_wallet.nonce);
            Node.walletState.setWalletBalance(tx.to, dest_balance_after, ws_snapshot, dest_wallet.nonce);

            if (!ws_snapshot)
            {
                setAppliedFlag(tx.id, block.blockNum);
            }

            return true;
        }

        // Go through a dictionary of block numbers and respective miners and reward them
        public static void rewardMiners(IDictionary<ulong, List<string>> blockSolutionsDictionary, bool ws_snapshot = false)
        {
            for (int i = 0; i < blockSolutionsDictionary.Count; i++)
            {
                ulong blockNum = blockSolutionsDictionary.Keys.ElementAt(i);

                // Stop rewarding miners after 5th year
                if(blockNum > 5256000)
                {
                    continue;
                }

                Block block = Node.blockChain.getBlock(blockNum);
                // Check if the block is valid
                if (block == null)
                    continue;

                List<string> miners_to_reward = blockSolutionsDictionary[blockNum];

                IxiNumber miners_count = new IxiNumber(miners_to_reward.Count);
                IxiNumber powRewardPart = Config.powReward / miners_count;

                //Logging.info(String.Format("Rewarding {0} IXI to block #{1} miners", powRewardPart.ToString(), blockNum));

                string checksum_source = "MINERS";
                foreach (string miner in miners_to_reward)
                {
                    // TODO add another address checksum here, just in case
                    // Update the wallet state
                    Wallet miner_wallet = Node.walletState.getWallet(miner, ws_snapshot);
                    IxiNumber miner_balance_before = miner_wallet.balance;
                    IxiNumber miner_balance_after = miner_balance_before + powRewardPart;
                    Node.walletState.setWalletBalance(miner, miner_balance_after, ws_snapshot, miner_wallet.nonce);

                    checksum_source += miner;
                }

                // Ignore rewards during snapshot
                if (ws_snapshot == false)
                {
                    // Set the powField as a checksum of all miners for this block
                    block.powField = Crypto.sha256(checksum_source);
                }

            }
        }

        // Get the current snapshot of transactions in the pool
        public static byte[] getBytes()
        {
            lock (transactions)
            {
                using (MemoryStream m = new MemoryStream())
                {
                    using (BinaryWriter writer = new BinaryWriter(m))
                    {
                        // Write the number of transactions
                        int num_transactions = transactions.Count();
                        writer.Write(num_transactions);

                        // Write each transactions
                        foreach (Transaction transaction in transactions.Select(e => e.Value))
                        {
                            byte[] transaction_data = transaction.getBytes();
                            int transaction_data_size = transaction_data.Length;
                            writer.Write(transaction_data_size);
                            writer.Write(transaction_data);
                        }
                    }
                    return m.ToArray();
                }
            }
        }

        public static bool syncFromBytes(byte[] bytes)
        {
            using (MemoryStream m = new MemoryStream(bytes))
            {
                using (BinaryReader reader = new BinaryReader(m))
                {
                    // Read the number of transactions
                    int num_transactions = reader.ReadInt32();

                    Logging.info(String.Format("Number of transactions in sync txpool: {0}", num_transactions));
                    if (num_transactions < 0)
                        return false;

                    try
                    {
                        for (int i = 0; i < num_transactions; i++)
                        {
                            int transaction_data_size = reader.ReadInt32();
                            if (transaction_data_size < 1)
                                continue;
                            byte[] transaction_bytes = reader.ReadBytes(transaction_data_size);
                            Transaction new_transaction = new Transaction(transaction_bytes);
                            lock (transactions)
                            {
                                transactions.Add(new_transaction.id, new_transaction);
                            }

                            Logging.info(String.Format("SYNC Transaction: {0}", new_transaction.id));

                        }
                    }
                    catch(Exception e)
                    {
                        Logging.error(string.Format("Error reading transaction pool: {0}", e.ToString()));
                        return false;
                    }

                }
            }

            return true;
        }


        // Clears all the transactions in the pool
        public static void clear()
        {
            lock(transactions)
            {
                transactions.Clear();
            }
        }

        // Returns the initial balance of a wallet by reversing all the transactions in the memory pool
        public static IxiNumber getInitialBalanceForWallet(string address, IxiNumber finalBalance)
        {
            // TODO: After redaction, this is no longer viable (will have to change logic to on longer depend on this function)
            IxiNumber initialBalance = finalBalance;
            lock (transactions)
            {
                // Go through each transaction and reverse it for the specific address
                foreach (Transaction transaction in transactions.Select(e=>e.Value))
                {
                    if (address.Equals(transaction.from))
                    {
                        initialBalance += transaction.amount;
                    }
                    else if (address.Equals(transaction.to))
                    {
                        initialBalance -= transaction.amount;
                    }
                }
            }
            return initialBalance;
        }

        public static bool hasTransaction(string txid)
        {
            lock(transactions)
            {
                return transactions.ContainsKey(txid);
            }
        }

        // TODO: transaction throttling code. Need to redesign this.
        static Dictionary<Socket, int> socketTransactionsPerBlock = new Dictionary<Socket, int>();
        // Resets the socket transaction limits
        public static void resetSocketTransactionLimits()
        {
            lock (socketTransactionsPerBlock)
            {

                socketTransactionsPerBlock.Clear();
            }
        }

        // Returns true if throttled, false otherwise
        public static bool checkSocketTransactionLimits(Socket socket)
        {
            if (Node.blockSync.synchronizing == false)
            {
                lock (socketTransactionsPerBlock)
                {
                    if (socketTransactionsPerBlock.ContainsKey(socket))
                    {
                        if (socketTransactionsPerBlock[socket] > Config.nodeNewTransactionsLimit)
                        {
                            Logging.info(string.Format("Throttled transaction. Limited to {0} / block", socketTransactionsPerBlock[socket]));
                            return true;
                        }
                        else
                        {
                            socketTransactionsPerBlock[socket] = socketTransactionsPerBlock[socket] + 1;
                        }
                    }
                    else
                    {
                        socketTransactionsPerBlock[socket] = 1;
                    }
                }
            }
            return false;
        }


    }
}
