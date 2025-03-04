﻿using DLT.Meta;
using DLT.Network;
using IXICore;
using IXICore.Meta;
using IXICore.Network;
using IXICore.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static IXICore.Transaction;

namespace DLT
{
    class TransactionPool
    {
        public static readonly Dictionary<string, Transaction> transactions = new Dictionary<string, Transaction>();

        static TransactionPool()
        {
        }

        private TransactionPool()
        {
        }

        public static bool verifyMultisigTransaction(Transaction transaction)
        {
            // multisig verification
            if (transaction.type == (int)Transaction.Type.MultisigTX || transaction.type == (int)Transaction.Type.ChangeMultisigWallet || transaction.type == (int)Transaction.Type.MultisigAddTxSignature)
            {
                // multiple "from" addresses are not supported
                if (transaction.fromList.Count != 1)
                {
                    Logging.warn(String.Format("Multisig transaction {{ {0} }} has multiple 'from' addresses, which is not allowed!", transaction.id));
                    return false;
                }
                object multisig_type = transaction.GetMultisigData();
                if (multisig_type == null)
                {
                    Logging.warn(String.Format("Multisig transaction {{ {0} }} has invalid multisig data attached!", transaction.id));
                    return false;
                }

                // we can use fromList.First because:
                //  a: verifyTransaction() checks that there is at least one fromAddress (if there isn't, totalAmount == 0 and transaction is failed before it gets here)
                //  b: at the start of this function, fromList is checked for fromList.Count > 1 and failed if so
                byte[] from_address = (new Address(transaction.pubKey, transaction.fromList.First().Key)).address;

                string orig_txid = "";
                byte[] signer_pub_key = null;
                byte[] signer_nonce = null;
                if (multisig_type is Transaction.MultisigTxData)
                {
                    var multisig_obj = (Transaction.MultisigTxData)multisig_type;
                    // regular multisig transaction
                    if (multisig_obj.origTXId != "")
                    {
                        Logging.info(String.Format("Multisig transaction {{ {0} }} adds signature for origin multisig transaction {{ {1} }}.", transaction.id, multisig_obj.origTXId));
                    }
                    else
                    {
                        Logging.info(String.Format("Multisig transaction {{ {0} }} is an origin multisig transaction.", transaction.id));
                    }
                    orig_txid = multisig_obj.origTXId;
                    signer_pub_key = multisig_obj.signerPubKey;
                    signer_nonce = multisig_obj.signerNonce;
                    if (transaction.type == (int)Transaction.Type.MultisigAddTxSignature)
                    {
                        if(orig_txid == null || orig_txid.Length > 100 || orig_txid.Length < 10)
                        {
                            Logging.warn(String.Format("Orig txid {0} is invalid.", orig_txid));
                            return false;
                        }

                        Transaction tmp_tx = getTransaction(orig_txid, 0);
                        if (tmp_tx == null)
                        {
                            if(!Node.walletStorage.isMyAddress((new Address(signer_pub_key, signer_nonce)).address))
                            {
                                Logging.warn(String.Format("Orig txid {0} doesn't exist.", orig_txid));
                                return false;
                            }
                        }else if(tmp_tx.applied != 0)
                        {
                            Logging.warn(String.Format("Orig txid {0} has already been applied.", orig_txid));
                            return false;
                        }else if (tmp_tx.type != (int)Transaction.Type.ChangeMultisigWallet && (tmp_tx.type != (int)Transaction.Type.MultisigTX))
                        {
                            Logging.warn(String.Format("Orig txid {0} is not a multisig transaction.", orig_txid));
                            return false;
                        }
                    }
                }
                if (multisig_type is Transaction.MultisigAddrAdd)
                {
                    var multisig_obj = (Transaction.MultisigAddrAdd)multisig_type;

                    Wallet tmp_w = Node.walletState.getWallet((new Address(transaction.pubKey, transaction.fromList.First().Key)).address);
                    if (tmp_w.isValidSigner(multisig_obj.addrToAdd))
                    {
                        Logging.warn(String.Format("Pubkey {0} is already in allowed multisig list for wallet {1}.", Base58Check.Base58CheckEncoding.EncodePlain(multisig_obj.addrToAdd), Base58Check.Base58CheckEncoding.EncodePlain(tmp_w.id)));
                        return false;
                    }

                    Logging.info(String.Format("Multisig change(add) transaction adds allowed signer {0} to address {1}.", Base58Check.Base58CheckEncoding.EncodePlain(multisig_obj.addrToAdd), Base58Check.Base58CheckEncoding.EncodePlain(transaction.pubKey)));

                    signer_pub_key = multisig_obj.signerPubKey;
                    signer_nonce = multisig_obj.signerNonce;
                }
                if (multisig_type is Transaction.MultisigAddrDel)
                {
                    var multisig_obj = (Transaction.MultisigAddrDel)multisig_type;

                    Logging.info(String.Format("Multisig change(del) transaction removes allowed signer {0} from wallet {1}.", Base58Check.Base58CheckEncoding.EncodePlain(multisig_obj.addrToDel), Base58Check.Base58CheckEncoding.EncodePlain(transaction.pubKey)));

                    signer_pub_key = multisig_obj.signerPubKey;
                    signer_nonce = multisig_obj.signerNonce;
                }
                if (multisig_type is Transaction.MultisigChSig)
                {
                    var multisig_obj = (Transaction.MultisigChSig)multisig_type;

                    Logging.info(String.Format("Multisig change(sig) transaction changes required signatures for wallet {0} to {1}.", Base58Check.Base58CheckEncoding.EncodePlain(transaction.pubKey), multisig_obj.reqSigs));

                    signer_pub_key = multisig_obj.signerPubKey;
                    signer_nonce = multisig_obj.signerNonce;
                }
                if(signer_pub_key == null)
                {
                    Logging.warn(String.Format("Multisig transaction {{ {0} }}, has a null signer pubkey!", transaction.id));
                }
                byte[] tx_signer_address = (new Address(signer_pub_key, signer_nonce)).address;

                Wallet w = Node.walletState.getWallet(from_address);
                if (!w.isValidSigner(tx_signer_address))
                {
                    Logging.warn(String.Format("Multisig transaction {{ {0} }} does not have a valid signature for wallet {1}.", transaction.id, Base58Check.Base58CheckEncoding.EncodePlain((w.id))));
                    return false;
                }
                // multisig tx can only be performed on multisig wallets
                if(w.type != WalletType.Multisig)
                {
                    // only exception is the "add signer multisig"
                    if(!(multisig_type is Transaction.MultisigAddrAdd))
                    {
                        Logging.warn(String.Format("Multisig transaction {{ {0} }} attempts to operate on a non-multisig wallet {1}.", transaction.id, Base58Check.Base58CheckEncoding.EncodePlain(w.id)));
                        return false;
                    }
                }
            }
            return true;
        }

        public static bool verifyPremineTransaction(Transaction transaction)
        {
            ulong block_height = IxianHandler.getLastBlockHeight();
            if(block_height > 5256000)
            {
                return true;
            }

            List<byte[]> premine_addresses = new List<byte[]>();
            premine_addresses.Add(Base58Check.Base58CheckEncoding.DecodePlain("13fiCRZHPqcCFvQvuggKEjDvFsVLmwoavaBw1ng5PdSKvCUGp"));
            premine_addresses.Add(Base58Check.Base58CheckEncoding.DecodePlain("16LUmwUnU9M4Wn92nrvCStj83LDCRwvAaSio6Xtb3yvqqqCCz"));
            byte[] tx_address = (new Address(transaction.pubKey)).address;
            foreach (byte[] premine_address in premine_addresses)
            {
                if (tx_address.SequenceEqual(premine_address))
                {
                    IxiNumber cur_balance = IxianHandler.getWalletBalance(premine_address);
                    if (block_height < 1051200)
                    {
                        if(cur_balance - transaction.amount - transaction.fee < new IxiNumber("900000000"))
                        {
                            return false;
                        }
                    }else if(block_height < 2102400)
                    {
                        if (cur_balance - transaction.amount - transaction.fee < new IxiNumber("800000000"))
                        {
                            return false;
                        }
                    }
                    else if (block_height < 3153600)
                    {
                        if (cur_balance - transaction.amount - transaction.fee < new IxiNumber("600000000"))
                        {
                            return false;
                        }
                    }
                    else if (block_height < 4204800)
                    {
                        if (cur_balance - transaction.amount - transaction.fee < new IxiNumber("400000000"))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        if (cur_balance - transaction.amount - transaction.fee < new IxiNumber("200000000"))
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
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
            else if (blocknum < 10)
            {
                Logging.warn(String.Format("Ignoring transaction before block 10."));
                return false;
            }
            else if (transaction.type == (int)Transaction.Type.Genesis)
            {
                Logging.warn(String.Format("Genesis transaction on block #{0} skipped. TXid: {1}.", blocknum, transaction.id));
                return false;
            }

            if(transaction.version > Transaction.maxVersion)
            {
                Logging.error("Received transaction {0} with a version higher than this node can handle, discarding the transaction.", transaction.id);
                return false;
            }

            // reject any transaction with block height 0
            if (transaction.blockHeight == 0)
            {
                Logging.warn(String.Format("Transaction without block height specified on block #{0} skipped. TXid: {1}.", blocknum, transaction.id));
                return false;
            }

            // lock transaction v2 with block v3
            if (transaction.version < 2 && Node.blockChain.getLastBlockVersion() >= 3)
            {
                Logging.warn(String.Format("Transaction version is too low, network is using v2 as the lowest valid version. TXid: {0}.", transaction.id));
                return false;
            }

            // Check the block height
            ulong minBh = 0;
            if (blocknum > ConsensusConfig.getRedactedWindowSize())
            {
                minBh = blocknum - ConsensusConfig.getRedactedWindowSize();
            }
            ulong highest_block_num = IxianHandler.getLastBlockHeight() + 5;
            if(Node.blockProcessor.highestNetworkBlockNum > highest_block_num)
            {
                highest_block_num = Node.blockProcessor.highestNetworkBlockNum + 5;
            }
            if (minBh > transaction.blockHeight || (transaction.blockHeight > blocknum + 5 && transaction.blockHeight > highest_block_num))
            {
                Logging.warn(String.Format("Incorrect block height for transaction {0}. Tx block height is {1}, expecting at least {2} and at most {3}", transaction.id, transaction.blockHeight, minBh, highest_block_num));
                return false;
            }

            // Prevent transaction spamming
            // Note: transactions that change multisig wallet parameters may have amount zero, since it will be ignored anyway
            if(transaction.type != (int)Transaction.Type.PoWSolution)
            if (transaction.amount == (long)0 && transaction.type != (int)Transaction.Type.ChangeMultisigWallet && transaction.type != (int)Transaction.Type.MultisigAddTxSignature)
            {
                    Logging.warn("Transaction amount was zero for txid {0}.", transaction.id);
                    return false;
            }

            if(transaction.amount < 0)
            {
                Logging.warn("Transaction amount was negative for txid {0}.", transaction.id);
                return false;
            }

            if(!verifyMultisigTransaction(transaction))
            {
                return false;
            }

            PendingTransactions.increaseReceivedCount(transaction.id);

            lock (transactions)
            {
                // Search for duplicates
                if (transactions.ContainsKey(transaction.id))
                {
                    return false;
                }
            }

            // Calculate the transaction checksum and compare it
            byte[] checksum = Transaction.calculateChecksum(transaction);
            if (checksum.SequenceEqual(transaction.checksum) == false)
            {
                Logging.warn(String.Format("Adding transaction {{ {0} }}, but checksum doesn't match!", transaction.id));
                return false;
            }

            IxiNumber totalAmount = new IxiNumber(0);
            foreach (var entry in transaction.fromList)
            {
                if (entry.Key.Length == 1 && entry.Key.First() != 0)
                {
                    Logging.warn(String.Format("Input nonce is 1 byte long but has an incorrect value for tx {{ {0} }}.", transaction.id));
                    return false;
                }
                else if (entry.Key.Length != 1 && entry.Key.Length != 16)
                {
                    Logging.warn(String.Format("Input nonce is not 1 or 16 bytes long for tx {{ {0} }}.", transaction.id));
                    return false;
                }

                if (entry.Value < 0)
                {
                    Logging.warn("Transaction amount was invalid for txid {0}.", transaction.id);
                    return false;
                }

                totalAmount += entry.Value;
                if (transaction.type != (int)Transaction.Type.PoWSolution
                    && transaction.type != (int)Transaction.Type.StakingReward
                    && transaction.type != (int)Transaction.Type.Genesis)
                {
                    if (Node.blockSync.synchronizing == false)
                    {
                        byte[] tmp_from_address = (new Address(transaction.pubKey, entry.Key)).address;


                        if (transaction.type != (int)Transaction.Type.ChangeMultisigWallet
                            && transaction.type != (int)Transaction.Type.MultisigAddTxSignature)
                        {
                            if (transaction.toList.ContainsKey(tmp_from_address))
                            {
                                // Prevent sending to the same address
                                Logging.warn(String.Format("To and from addresses are the same in transaction {{ {0} }}.", transaction.id));
                                return false;
                            }
                        }

                        Wallet tmp_wallet = Node.walletState.getWallet(tmp_from_address);

                        
                        if(transaction.type == (int)Transaction.Type.MultisigTX
                            || transaction.type == (int)Transaction.Type.ChangeMultisigWallet
                            || transaction.type == (int)Transaction.Type.MultisigAddTxSignature)
                        {
                            if (tmp_wallet.type != WalletType.Multisig)
                            {
                                object ms_data = transaction.GetMultisigData();
                                if (ms_data is Transaction.MultisigAddrAdd)
                                {
                                    // wallet type can be normal or multisig
                                }
                                else
                                {
                                    Logging.warn(String.Format("Attempted to use normal address with multisig transaction {{ {0} }}.", transaction.id));
                                    return false;
                                }
                            }
                        }else
                        {
                            if (tmp_wallet.type != WalletType.Normal)
                            {
                                Logging.warn(String.Format("Attempted to use a non-normal address with normal transaction {{ {0} }}.", transaction.id));
                                return false;
                            }
                        }

                        // Verify the transaction against the wallet state
                        IxiNumber fromBalance = tmp_wallet.balance;

                        if (fromBalance < entry.Value)
                        {
                            // Prevent overspending
                            Logging.warn(String.Format("Attempted to overspend with transaction {{ {0} }}.", transaction.id));
                            return false;
                        }
                    }
                }
            }

            if (totalAmount != transaction.amount + transaction.fee)
            {
                Logging.warn(string.Format("Total amount {0} specified by the transaction {1} inputs is different than the actual total amount {2}.", (transaction.amount + transaction.fee).ToString(), transaction.id, totalAmount.ToString()));
                return false;
            }

            totalAmount = new IxiNumber(0);
            foreach (var entry in transaction.toList)
            {
                if (!Address.validateChecksum(entry.Key))
                {
                    Logging.warn(String.Format("Adding transaction {{ {0} }}, but to address is incorrect!", transaction.id));
                    return false;
                }
                if (entry.Value < 0)
                {
                    Logging.warn("Transaction amount was invalid for txid {0}.", transaction.id);
                    return false;
                }
                totalAmount += entry.Value;
            }

            if(totalAmount != transaction.amount)
            {
                Logging.warn(string.Format("Total amount {0} specified by the transaction {1} outputs is different than the actual total amount {2}.", transaction.amount.ToString(), transaction.id, totalAmount.ToString()));
                return false;
            }

            // Special case for PoWSolution transactions
            if (transaction.type == (int)Transaction.Type.PoWSolution)
            {
                ulong tmp = 0;
                string tmp2 = "";
                if (!Node.blockSync.synchronizing && !verifyPoWTransaction(transaction, out tmp, out tmp2))
                {
                    return false;
                }
                // TODO: pre-validate the transaction in such a way it doesn't affect performance
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

                IxiNumber expectedFee = transaction.calculateMinimumFee(ConsensusConfig.transactionPrice);
                if (transaction.version == 0)
                {
                    expectedFee = ConsensusConfig.transactionPrice;
                }
                // Verify if the transaction contains the minimum fee
                if (transaction.fee < expectedFee)
                {
                    // Prevent transactions that can't pay the minimum fee
                    Logging.warn(String.Format("Transaction fee does not cover minimum fee for {{ {0} }}.", transaction.id));
                    return false;
                }
            }
            /*var sw = new System.Diagnostics.Stopwatch();
            sw.Start();*/

            // Extract the public key if found. Used for transaction verification.
            byte[] pubkey = null;
            byte[] signer_nonce = null; // used for multi sig

            if (transaction.type == (int)Transaction.Type.Genesis ||
                transaction.type == (int)Transaction.Type.StakingReward)
            {
                return true;
            }
            else if (transaction.type == (int)Transaction.Type.MultisigTX
                || transaction.type == (int)Transaction.Type.ChangeMultisigWallet
                || transaction.type == (int)Transaction.Type.MultisigAddTxSignature)
            {
                object ms_data = transaction.GetMultisigData();
                if (ms_data is Transaction.MultisigAddrAdd)
                {
                    pubkey = ((MultisigAddrAdd)ms_data).signerPubKey;
                    signer_nonce = ((MultisigAddrAdd)ms_data).signerNonce;
                }
                else if (ms_data is Transaction.MultisigAddrDel)
                {
                    pubkey = ((MultisigAddrDel)ms_data).signerPubKey;
                    signer_nonce = ((MultisigAddrDel)ms_data).signerNonce;
                }
                else if (ms_data is Transaction.MultisigChSig)
                {
                    pubkey = ((MultisigChSig)ms_data).signerPubKey;
                    signer_nonce = ((MultisigChSig)ms_data).signerNonce;
                }
                else if (ms_data is Transaction.MultisigTxData)
                {
                    pubkey = ((MultisigTxData)ms_data).signerPubKey;
                    signer_nonce = ((MultisigTxData)ms_data).signerNonce;
                }
            }
            else
            {
                pubkey = Node.walletState.getWallet((new Address(transaction.pubKey)).address).publicKey;
                // Generate an address from the public key and compare it with the sender
                if (pubkey == null)
                {
                    // There is no supplied public key, extract it from the data section
                    pubkey = transaction.pubKey;
                }
            }

            if (pubkey == null || pubkey.Length < 32 || pubkey.Length > 2500)
            {
                Logging.warn(string.Format("Invalid pubkey for transaction id: {0}", transaction.id));
                return false;
            }

            if (signer_nonce != null && signer_nonce.Length > 16)
            {
                Logging.warn(string.Format("Invalid nonce for transaction id: {0}", transaction.id));
                return false;
            }

            // Finally, verify the signature
            if (transaction.verifySignature(pubkey, signer_nonce) == false)
            {
                // Transaction signature is invalid
                Logging.warn(string.Format("Invalid signature for transaction id: {0}", transaction.id));
                return false;
            }

            if(!verifyPremineTransaction(transaction))
            {
                Logging.warn("Cannot spend so much premine yet, txid: {0}", transaction.id);
                return false;
            }
            /*sw.Stop();
            TimeSpan elapsed = sw.Elapsed;
            Logging.info(string.Format("VerifySignature duration: {0}ms", elapsed.TotalMilliseconds));*/

            return true;
        }

        public static bool setAppliedFlag(string txid, ulong blockNum, bool add_to_storage = true)
        {
            Transaction t;
            lock (transactions)
            {
                if (transactions.ContainsKey(txid))
                {
                    t = transactions[txid];
                    t.applied = blockNum;

                    if (Node.walletStorage.isMyAddress((new Address(t.pubKey)).address) || Node.walletStorage.extractMyAddressesFromAddressList(t.toList) != null)
                    {
                        if (!t.fromLocalStorage)
                        {
                            ActivityStorage.updateStatus(Encoding.UTF8.GetBytes(t.id), ActivityStatus.Final, t.applied);
                        }
                        PendingTransactions.remove(t.id);
                    }

                    if (t.applied == 0)
                    {
                        Logging.error("An error occured while adding tx " + txid + " to storage, applied was 0.");
                        return false;
                    }

                    PendingTransactions.remove(t.id);

                    if (add_to_storage)
                    {
                        bool insertTx = true;
                        if (Node.blockSync.synchronizing && Config.recoverFromFile)
                        {
                            insertTx = false;
                        }
                        if (insertTx)
                        {
                            Meta.Storage.insertTransaction(t);
                        }
                    }

                    // Send transaction events to all subscribed clients
                    // TODO: optimize this further to decrease cpu time in the current thread
                    broadcastAddTransactionEvent(t);

                    return true;
                }
            }
            return false;
        }

        public static List<string> getRelatedMultisigTransactions(string txid, Block block, bool remove_failed_transactions = true)
        {
            lock(transactions)
            {
                List<string> related_transaction_ids = new List<string>();

                List<Transaction> failed_transactions = new List<Transaction>();
                List<byte[]> signer_addresses = new List<byte[]>();
                List<string> tmp_transactions = transactions.Keys.ToList();
                if(block != null)
                {
                    tmp_transactions = block.transactions;
                }
                Transaction orig_tx = getTransaction(txid);
                if(orig_tx != null)
                {
                    object orig_ms_data = orig_tx.GetMultisigData();
                    byte[] signer_pub_key = null;
                    byte[] signer_nonce = null;
                    if (orig_ms_data is Transaction.MultisigAddrAdd)
                    {
                        var multisig_obj = (Transaction.MultisigAddrAdd)orig_ms_data;
                        signer_pub_key = multisig_obj.signerPubKey;
                        signer_nonce = multisig_obj.signerNonce;
                    }
                    else if (orig_ms_data is Transaction.MultisigAddrDel)
                    {
                        var multisig_obj = (Transaction.MultisigAddrDel)orig_ms_data;
                        signer_pub_key = multisig_obj.signerPubKey;
                        signer_nonce = multisig_obj.signerNonce;
                    }
                    else if (orig_ms_data is Transaction.MultisigChSig)
                    {
                        var multisig_obj = (Transaction.MultisigChSig)orig_ms_data;
                        signer_pub_key = multisig_obj.signerPubKey;
                        signer_nonce = multisig_obj.signerNonce;
                    }
                    else if (orig_ms_data is Transaction.MultisigTxData)
                    {
                        var multisig_obj = (Transaction.MultisigTxData)orig_ms_data;
                        signer_pub_key = multisig_obj.signerPubKey;
                        signer_nonce = multisig_obj.signerNonce;
                    }
                    else
                    {
                        Logging.warn(String.Format("Multisig transaction {{ {0} }} has invalid data!", orig_tx.id));
                        // unknown MS transaction, discard
                        failed_transactions.Add(orig_tx);
                        orig_tx = null;
                    }
                    if (orig_tx != null)
                    {
                        byte[] signer_address = ((new Address(signer_pub_key, signer_nonce)).address);
                        signer_addresses.Add(signer_address);
                    } else
                    {
                        return related_transaction_ids;
                    }
                }

                foreach (var tx_key in tmp_transactions)
                {
                    if (!transactions.ContainsKey(tx_key))
                    {
                        continue;
                    }
                    var tx = transactions[tx_key];
                    if (tx == null)
                    {
                        continue;
                    }
                    if(tx.type == (int)Transaction.Type.MultisigAddTxSignature)
                    {
                        object multisig_type = tx.GetMultisigData();
                        string orig_txid = "";
                        byte[] signer_pub_key = null;
                        byte[] signer_nonce = null;
                        if (multisig_type is Transaction.MultisigTxData)
                        {
                            var multisig_obj = (Transaction.MultisigTxData)multisig_type;
                            orig_txid = multisig_obj.origTXId;
                            signer_pub_key = multisig_obj.signerPubKey;
                            signer_nonce = multisig_obj.signerNonce;
                        }

                        if (orig_txid == "" || orig_txid == null)
                        {
                            Logging.warn(String.Format("Multisig transaction {{ {0} }} has a null orig txid!", tx.id));
                            failed_transactions.Add(tx);
                            continue;
                        }

                        if (signer_pub_key == null)
                        {
                            Logging.warn(String.Format("Multisig transaction {{ {0} }} signer_pub_key is null - orig multisig transaction: {1}!", tx.id, orig_txid));
                            failed_transactions.Add(tx);
                            continue;
                        }

                        if (orig_txid == txid)
                        {
                            if (orig_tx == null)
                            {
                                Logging.warn(String.Format("Multisig transaction {{ {0} }} signs a missing orig multisig transaction {1}!", tx.id, orig_txid));
                                failed_transactions.Add(tx);
                                continue;
                            }

                            byte[] signer_address = ((new Address(signer_pub_key, signer_nonce)).address);
                            if (signer_addresses.Contains(signer_address, new ByteArrayComparer()))
                            {
                                Logging.warn(String.Format("Multisig transaction {{ {0} }} signs an already signed transaction {1} by this address {2}!",
                                    tx.id, orig_txid, Base58Check.Base58CheckEncoding.EncodePlain(signer_address)));
                                failed_transactions.Add(tx);
                                continue;
                            }

                            Wallet orig = Node.walletState.getWallet((new Address(tx.pubKey, tx.fromList.First().Key).address));
                            if (!orig.isValidSigner(signer_address))
                            {
                                Logging.warn(String.Format("Tried to use Multisig transaction {{ {0} }} without being an actual owner {1}!",
                                    txid, Base58Check.Base58CheckEncoding.EncodePlain(signer_address)));
                                failed_transactions.Add(tx);
                                continue;
                            }

                            signer_addresses.Add(signer_address);
                            related_transaction_ids.Add(tx_key);
                        }
                    }
                }

                if (remove_failed_transactions)
                {
                    // Remove all failed transactions from the TxPool and block
                    foreach (Transaction tx in failed_transactions)
                    {
                        Logging.warn(String.Format("Removing failed ms transaction #{0} from pool.", tx.id));
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
                            Logging.error(String.Format("Error, attempting to remove failed ms transaction #{0} from pool, that was already applied.", tx.id));
                        }
                    }
                }

                return related_transaction_ids;
            }
        }

        public static int getNumRelatedMultisigTransactions(string txid, Block block)
        {
            return getRelatedMultisigTransactions(txid, block, false).Count();
        }

        private static void addTransactionToActivityStorage(Transaction transaction)
        {
            Activity activity = null;
            int type = -1;
            IxiNumber value = transaction.amount;
            List<byte[]> wallet_list = null;
            byte[] wallet = null;
            byte[] primary_address = (new Address(transaction.pubKey)).address;
            if (Node.walletStorage.isMyAddress(primary_address))
            {
                wallet = primary_address;
                type = (int)ActivityType.TransactionSent;
                if (transaction.type == (int)Transaction.Type.PoWSolution)
                {
                    type = (int)ActivityType.MiningReward;
                    value = Miner.calculateRewardForBlock(BitConverter.ToUInt64(transaction.data, 0));
                }
            }else
            {
                wallet_list = Node.walletStorage.extractMyAddressesFromAddressList(transaction.toList);
                if (wallet_list != null)
                {
                    type = (int)ActivityType.TransactionReceived;
                    if (transaction.type == (int)Transaction.Type.StakingReward)
                    {
                        type = (int)ActivityType.StakingReward;
                    }
                }
            }
            if (type != -1)
            {
                int status = (int)ActivityStatus.Pending;
                if (transaction.applied > 0)
                {
                    status = (int)ActivityStatus.Final;
                }
                if(wallet_list != null)
                {
                    foreach (var entry in wallet_list)
                    {
                        activity = new Activity(Node.walletStorage.getSeedHash(), Base58Check.Base58CheckEncoding.EncodePlain(entry), Base58Check.Base58CheckEncoding.EncodePlain(primary_address), transaction.toList, type, Encoding.UTF8.GetBytes(transaction.id), transaction.toList[entry].ToString(), transaction.timeStamp, status, transaction.applied, transaction.id);
                        ActivityStorage.insertActivity(activity);
                    }
                }
                else if(wallet != null)
                {
                    activity = new Activity(Node.walletStorage.getSeedHash(), Base58Check.Base58CheckEncoding.EncodePlain(wallet), Base58Check.Base58CheckEncoding.EncodePlain(primary_address), transaction.toList, type, Encoding.UTF8.GetBytes(transaction.id), value.ToString(), transaction.timeStamp, status, transaction.applied, transaction.id);
                    ActivityStorage.insertActivity(activity);
                }
            }
        }

        // Adds a non-applied transaction to the memory pool
        // Returns true if the transaction is added to the pool, false otherwise
        public static bool addTransaction(Transaction transaction, bool no_broadcast = false, RemoteEndpoint skipEndpoint = null, bool verifyTx = true)
        {
            if (verifyTx)
            {
                if (!verifyTransaction(transaction))
                {
                    return false;
                }
            }else
            {
                if(!transaction.checksum.SequenceEqual(Transaction.calculateChecksum(transaction)))
                {
                    Logging.warn(String.Format("Adding transaction {{ {0} }}, but checksum doesn't match!", transaction.id));
                    return false;
                }
            }

            //Logging.info(String.Format("Accepted transaction {{ {0} }}, amount: {1}", transaction.id, transaction.amount));
            lock (transactions)
            {
                if (transactions.ContainsKey(transaction.id))
                {
                    Logging.warn(String.Format("Duplicate transaction {{ {0} }}: already exists in the Transaction Pool.", transaction.id));
                    return false;
                }
                else
                {
                    transactions.Add(transaction.id, transaction);
                    if (!transaction.fromLocalStorage)
                    {
                        addTransactionToActivityStorage(transaction);
                    }
                }
            }


            //   Logging.info(String.Format("Transaction {{ {0} }} has been added.", transaction.id, transaction.amount));
            //Console.WriteLine("Transaction {{ {0} }} has been added.", transaction.id, transaction.amount);
            if (ConsoleHelpers.verboseConsoleOutput)
                Console.Write("$");

            if (Node.blockSync.synchronizing == true)
            {
                return true;
            }

            // Broadcast this transaction to the network
            if (no_broadcast == false)
                CoreProtocolMessage.broadcastProtocolMessage(new char[] { 'M', 'H' }, ProtocolMessageCode.newTransaction, transaction.getBytes(), null, skipEndpoint);

            // Send transaction events to all subscribed clients
            // TODO: optimize this further to decrease cpu time in the current thread
            broadcastAddTransactionEvent(transaction);


            return true;
        }

        // Send transaction events to all subscribed clients
        public static void broadcastAddTransactionEvent(Transaction transaction)
        {
            if(Node.blockSync.synchronizing)
            {
                return;
            }

            // Send transaction FROM event
            byte[] from_addr = new Address(transaction.pubKey).address;
            CoreProtocolMessage.broadcastEventDataMessage(NetworkEvents.Type.transactionFrom, from_addr, ProtocolMessageCode.newTransaction, transaction.getBytes(true), Encoding.UTF8.GetBytes(transaction.id));

            // Send transaction TO event
            foreach (var entry in transaction.toList)
            {
                byte[] addr = new byte[entry.Key.Length];
                Array.Copy(entry.Key, addr, addr.Length);
                CoreProtocolMessage.broadcastEventDataMessage(NetworkEvents.Type.transactionTo, addr, ProtocolMessageCode.newTransaction, transaction.getBytes(true), Encoding.UTF8.GetBytes(transaction.id));
            }
        }

        // Attempts to retrieve a transaction from memory or from storage
        // Returns null if no transaction is found
        public static Transaction getTransaction(string txid, ulong block_num = 0, bool search_in_storage = false)
        {
            Transaction transaction = null;

            bool compacted_transaction = false;

            lock(transactions)
            {
                //Logging.info(String.Format("Looking for transaction {{ {0} }}. Pool has {1}.", txid, transactions.Count));
                compacted_transaction = transactions.ContainsKey(txid);
                transaction = compacted_transaction ? transactions[txid] : null;
            }

            if (transaction != null)
                return transaction;

            if (search_in_storage || compacted_transaction)
            {
                // No transaction found in memory, look into storage

                /*var sw = new System.Diagnostics.Stopwatch();
                sw.Start();*/
                transaction = Storage.getTransaction(txid, block_num);
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

        public static Transaction[] getLastTransactions()
        {
            lock (transactions)
            {
                List<Transaction> full_tx_list = new List<Transaction>();
                foreach (var entry in transactions)
                {
                    Transaction tx = entry.Value;
                    if (tx == null)
                    {
                        tx = getTransaction(entry.Key);
                    }
                    full_tx_list.Add(tx);
                }
                return full_tx_list.ToArray();
            }
        }

        public static Transaction[] getAppliedTransactions()
        {
            lock (transactions)
            {
                List<Transaction> full_tx_list = new List<Transaction>();
                var tx_list =  transactions.Where(x => x.Value == null || x.Value.applied != 0).ToArray();
                foreach(var entry in tx_list)
                {
                    Transaction tx = entry.Value;
                    if(tx == null)
                    {
                        tx = getTransaction(entry.Key);
                    }
                    full_tx_list.Add(tx);
                }
                return full_tx_list.ToArray();
            }
        }

        public static Transaction[] getUnappliedTransactions()
        {
            lock(transactions)
            {
                return transactions.Select(e => e.Value).Where(x => x != null && x.applied == 0).ToArray();
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
        public static bool verifyPoWTransaction(Transaction tx, out ulong blocknum, out string nonce, int block_version = -1, bool verify_pow = true)
        {
            blocknum = 0;
            nonce = "";

            if (tx.type != (int)Transaction.Type.PoWSolution)
                return false;

            // Extract the block number and nonce
            using (MemoryStream m = new MemoryStream(tx.data))
            {
                using (BinaryReader reader = new BinaryReader(m))
                {
                    blocknum = reader.ReadUInt64();
                    nonce = reader.ReadString();
                }
            }

            if(blocknum > IxianHandler.getLastBlockHeight())
            {
                return false;
            }

            // ignore PoW solutions for first 100 blocks in the redacted window
            if(block_version >= 3 && blocknum + ConsensusConfig.getRedactedWindowSize(block_version) - 100 < IxianHandler.getLastBlockHeight())
            {
                return false;
            }

            try
            {
                Block block = Node.blockChain.getBlock(blocknum); // TODO TODO TODO TODO TODO we can pass the false, false parameters here when the new storage solution is enabled

                if(block == null)
                {
                    return false;
                }

                // Check if the block has an empty PoW field
                if (block.powField != null)
                {
                    Logging.warn("PoW already applied");
                    return false;
                }

                if (verify_pow == false || tx.powVerified)
                {
                    return true;
                }

                byte[] primary_address = (new Address(tx.pubKey)).address;

                if (block.version == BlockVer.v0)
                {
                    // Verify the nonce
                    if ((tx.fromLocalStorage && !Config.fullStorageDataVerification) || Miner.verifyNonce_v0(nonce, blocknum, primary_address, block.difficulty))
                    {
                        tx.powVerified = true;
                        return true;
                    }
                }
                else if (block.version == BlockVer.v1)
                {
                    // Verify the nonce
                    if ((tx.fromLocalStorage && !Config.fullStorageDataVerification) || Miner.verifyNonce_v1(nonce, blocknum, primary_address, block.difficulty))
                    {
                        tx.powVerified = true;
                        return true;
                    }
                }
                else if (block.version <= BlockVer.v4)
                {
                    // Verify the nonce
                    if ((tx.fromLocalStorage && !Config.fullStorageDataVerification) || Miner.verifyNonce_v2(nonce, blocknum, primary_address, block.difficulty))
                    {
                        tx.powVerified = true;
                        return true;
                    }
                }
                else // >= 5
                {
                    // Verify the nonce
                    if ((tx.fromLocalStorage && !Config.fullStorageDataVerification) || Miner.verifyNonce_v3(nonce, blocknum, primary_address, block.difficulty))
                    {
                        tx.powVerified = true;
                        return true;
                    }
                }
            }
            catch(Exception e)
            {
                Logging.warn(string.Format("Error verifying PoW Transaction: {0}. Message: {1}", tx.id, e.Message));
            }

            return false;
        }

        public static bool setAppliedFlagToTransactionsFromBlock(Block b)
        {
            if (b == null)
            {
                return true;
            }
            lock (transactions)
            {
                Dictionary<ulong, List<object[]>> blockSolutionsDictionary = new Dictionary<ulong, List<object[]>>();
                foreach (string txid in b.transactions)
                {
                    Transaction tx = getTransaction(txid, b.blockNum);
                    if (tx == null)
                    {
                        Logging.error(String.Format("Attempted to set applied to transaction from block #{0} ({1}), but transaction {{ {2} }} was missing.",
                            b.blockNum, Crypto.hashToString(b.blockChecksum), txid));
                        return false;
                    }
                    applyPowTransaction(tx, b, blockSolutionsDictionary, null, true, !tx.fromLocalStorage);
                    setAppliedFlag(txid, b.blockNum, !tx.fromLocalStorage);
                }
                // set PoW fields
                for (int i = 0; i < blockSolutionsDictionary.Count; i++)
                {
                    ulong blockNum = blockSolutionsDictionary.Keys.ElementAt(i);

                    // Stop rewarding miners after 5th year
                    if (blockNum >= 5256000)
                    {
                        continue;
                    }

                    Block block = Node.blockChain.getBlock(blockNum, false, false);
                    // Check if the block is valid
                    if (block == null)
                        continue;

                    // Set the powField as a checksum of all miners for this block
                    block.powField = BitConverter.GetBytes(b.blockNum);
                }
            }
            return true;
        }

        public static bool applyStakingTransactionsFromBlock(Block block, List<Transaction> failed_staking_transactions, bool ws_snapshot = false)
        {
            // TODO: move this to a seperate function. Left here for now for dev purposes
            // Apply any staking transactions in the pool at this moment
            List<Transaction> staking_txs = null;
            if (ws_snapshot)
            {
                staking_txs = Node.blockProcessor.generateStakingTransactions(block.blockNum - 6, block.version, ws_snapshot, block.timestamp);
            }
            else
            {
                lock (transactions)
                {
                    staking_txs = transactions.Select(e => e.Value).Where(x => x != null && x.type == (int)Transaction.Type.StakingReward && x.applied == 0).ToList();
                }
            }

            // Maintain a list of stakers
            List<byte[]> blockStakers = new List<byte[]>();

            List<string> stakingTxIds = block.transactions.FindAll(x => x.StartsWith("stk-"));

            foreach (Transaction tx in staking_txs)
            {
                if (stakingTxIds.Exists(x => x == tx.id))
                {
                    stakingTxIds.Remove(tx.id);
                } else
                {
                    Logging.error(String.Format("Invalid staking txid in transaction pool {0}, removing from pool.", tx.id));
                    lock(transactions)
                    {
                        transactions.Remove(tx.id);
                    }
                    continue;
                }

                if (tx.applied > 0)
                    continue;

                string[] split_str = tx.id.Split(new string[] { "-" }, StringSplitOptions.None);
                ulong txbnum = Convert.ToUInt64(split_str[1]);

                if (txbnum != block.blockNum - 6)
                {
                    return false;
                }

                // Special case for Staking Reward transaction
                // Do not apply them if we are synchronizing
                // TODO: note that this can backfire when recovering completely from a file
                if (Node.blockSync.synchronizing && Config.recoverFromFile == false && Config.storeFullHistory == false && Config.fullStorageDataVerification == false)
                    continue;
               
                if (applyStakingTransaction(tx, block, failed_staking_transactions, blockStakers, ws_snapshot))
                {
                    //Console.WriteLine("!!! APPLIED STAKE {0}", tx.id);
                    continue;
                }else
                {
                    return false;
                }
            }

            if(stakingTxIds.Count > 0)
            {
                return false;
            }

            return true;
        }

        // This applies all the transactions from a block to the actual walletstate.
        // It removes the failed transactions as well from the pool and block.
        public static bool applyTransactionsFromBlock(Block block, bool ws_snapshot = false)
        {
            if (block == null)
            {
                return false;
            }

            try
            {
                // Maintain a dictionary of block solutions and the corresponding miners for solved blocks
                IDictionary<ulong, List<object[]>> blockSolutionsDictionary = new Dictionary<ulong, List<object[]>>();

                // Maintain a list of failed transactions to remove them from the TxPool in one go
                List<Transaction> failed_transactions = new List<Transaction>();

                List<Transaction> failed_staking_transactions = new List<Transaction>();

                if(!applyStakingTransactionsFromBlock(block, failed_staking_transactions, ws_snapshot))
                {
                    return false;
                }

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
                        if (Node.blockSync.synchronizing && !Config.recoverFromFile && !Config.storeFullHistory)
                        {
                            if (getTransaction(txid, block.blockNum) == null)
                            {
                                Logging.info(string.Format("Missing staking transaction during sync: {0}", txid));
                            }
                        }
                        continue;
                    }

                    Transaction tx = getTransaction(txid, block.blockNum);

                    if (tx == null)
                    {
                        Logging.error(String.Format("Attempted to apply transactions from block #{0} ({1}), but transaction {{ {2} }} was missing.",
                            block.blockNum, Crypto.hashToString(block.blockChecksum), txid));
                        return false;
                    }

                    if (tx.type == (int)Transaction.Type.StakingReward)
                    {
                        continue;
                    }

                    if (tx.applied == block.blockNum)
                    {
                        continue;
                    }else if (tx.applied != 0)
                    {
                        Logging.error(String.Format("Attempted to apply transactions from block #{0} ({1}), but transaction {{ {2} }} was already applied in block #{3}.",
                            block.blockNum, Crypto.hashToString(block.blockChecksum), txid, tx.applied));
                        return false;
                    } 

                    // Special case for Genesis transactions
                    if (applyGenesisTransaction(tx, block, failed_transactions, ws_snapshot))
                    {
                        continue;
                    }


                    if(block.version >= BlockVer.v3)
                    {
                        byte[] tmp_address = (new Address(tx.pubKey)).address;
                        // Update the walletstate public key
                        byte[] pubkey = Node.walletState.getWallet(tmp_address, ws_snapshot).publicKey;
                        // Generate an address from the public key and compare it with the sender
                        if (pubkey == null)
                        {
                            // There is no supplied public key, extract it from transaction
                            pubkey = tx.pubKey;
                            if (pubkey != null)
                            {
                                // Update the walletstate public key
                                Node.walletState.setWalletPublicKey(tmp_address, pubkey, ws_snapshot);
                            }
                        }
                    }

                    // Special case for PoWSolution transactions
                    if (applyPowTransaction(tx, block, blockSolutionsDictionary, failed_transactions, ws_snapshot))
                    {
                        continue;
                    }

                    // Check the transaction amount
                    if (tx.amount == (long)0
                        && tx.type != (int)Transaction.Type.ChangeMultisigWallet
                        && tx.type != (int)Transaction.Type.MultisigAddTxSignature
                        )
                    {
                        failed_transactions.Add(tx);
                        continue;
                    }

                    if (block.version < BlockVer.v3)
                    {
                        byte[] tmp_address = (new Address(tx.pubKey)).address;
                        // Update the walletstate public key
                        byte[] pubkey = Node.walletState.getWallet(tmp_address, ws_snapshot).publicKey;
                        // Generate an address from the public key and compare it with the sender
                        if (pubkey == null)
                        {
                            // There is no supplied public key, extract it from transaction
                            pubkey = tx.pubKey;
                            if (pubkey != null)
                            {
                                // Update the walletstate public key
                                Node.walletState.setWalletPublicKey(tmp_address, pubkey, ws_snapshot);
                            }
                        }
                    }

                    // Special case for Multisig
                    if (tx.type == (int)Transaction.Type.MultisigTX)
                    {
                        List<string> related_tx_ids = applyMultisigTransaction(tx, block, failed_transactions, ws_snapshot);
                        if(related_tx_ids == null)
                        {
                            Logging.error(string.Format("Block #{0} has failed multisig transactions, rejecting the block.", block.blockNum));
                            return false;
                        }else
                        {
                            applyMultisigRelatedTransactions(related_tx_ids, block, failed_transactions, ws_snapshot);
                        }
                        continue;
                    }
                    if (tx.type == (int)Transaction.Type.ChangeMultisigWallet)
                    {
                        List<string> related_tx_ids = applyMultisigChangeTransaction(tx, block, failed_transactions, ws_snapshot);
                        if (related_tx_ids == null)
                        {
                            Logging.error(string.Format("Block #{0} has failed multisig transactions, rejecting the block.", block.blockNum));
                            return false;
                        }
                        else
                        {
                            applyMultisigRelatedTransactions(related_tx_ids, block, failed_transactions, ws_snapshot);
                        }
                        continue;
                    }else if(tx.type == (int)Transaction.Type.MultisigAddTxSignature)
                    {
                        // continue as this is processed by applyMultisigRelatedTransactions
                        continue;
                    }

                    // If we reached this point, it means this is a normal transaction
                    applyNormalTransaction(tx, block, failed_transactions, ws_snapshot);

                }

                // Finally, Check if we have any miners to reward
                if (blockSolutionsDictionary.Count > 0)
                {
                    rewardMiners(block.blockNum, blockSolutionsDictionary, ws_snapshot);
                }

                // Clear the solutions dictionary
                blockSolutionsDictionary.Clear();

                // double-check if there are any unapplied transactions and if so reject the block
                if (!ws_snapshot)
                {
                    foreach (string txid in block.transactions)
                    {
                        Transaction tx = getTransaction(txid, block.blockNum);
                        if (tx == null || tx.applied != block.blockNum)
                        {
                            Logging.error(string.Format("Block #{0} has unapplied transactions, rejecting the block.", block.blockNum));
                            return false;
                        }
                    }
                }

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
            }
            catch (Exception e)
            {
                Logging.error(string.Format("Error applying transactions from block #{0}. Message: {1}", block.blockNum, e));
                return false;
            }
            
            return true;
        }

        // Checks if a transaction is a pow transaction and applies it.
        // Returns true if it's a PoW transaction, otherwise false
        // be careful when changing/updating ws_snapshot related things in this function as the parameter relies on sync as well
        public static bool applyPowTransaction(Transaction tx, Block block, IDictionary<ulong, List<object[]>> blockSolutionsDictionary, List<Transaction> failedTransactions, bool ws_snapshot = false, bool verify_pow = true)
        {
            if (tx.type != (int)Transaction.Type.PoWSolution)
            {
                return false;
            }

            ulong minBh = 0;
            if (block.blockNum > ConsensusConfig.getRedactedWindowSize(block.version))
            {
                minBh = block.blockNum - ConsensusConfig.getRedactedWindowSize(block.version);
            }
            // Check the block height
            if (minBh > tx.blockHeight || tx.blockHeight > block.blockNum)
            {
                Logging.warn(String.Format("Incorrect block height for transaction {0}. Tx block height is {1}, expecting at least {2} and at most {3}", tx.id, tx.blockHeight, minBh, block.blockNum + 5));
                failedTransactions.Add(tx);
                return false;
            }

            // Update the block's applied field
            if (!ws_snapshot)
            {
                setAppliedFlag(tx.id, block.blockNum);
            }

            // Verify if the solution is correct
            if (verifyPoWTransaction(tx, out ulong powBlockNum, out string nonce, block.version, verify_pow) == true)
            {
                // Check if we already have a key matching the block number
                if (blockSolutionsDictionary.ContainsKey(powBlockNum) == false)
                {
                    blockSolutionsDictionary[powBlockNum] = new List<object[]>();
                }
                byte[] primary_address = (new Address(tx.pubKey)).address;
                if (block.version < BlockVer.v2)
                {
                    blockSolutionsDictionary[powBlockNum].Add(new object[3] { primary_address, nonce, tx });
                }
                else
                {
                    if (!blockSolutionsDictionary[powBlockNum].Exists(x => ((byte[])x[0]).SequenceEqual(primary_address) && (string)x[1] == nonce))
                    {
                        // Add the miner to the block number dictionary reward list
                        blockSolutionsDictionary[powBlockNum].Add(new object[3] { primary_address, nonce, tx });
                    }
                    else
                    {
                        if (failedTransactions != null)
                        {
                            failedTransactions.Add(tx);
                        }
                    }
                }
            }else
            {
                if (failedTransactions != null)
                {
                    failedTransactions.Add(tx);
                }
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
            foreach (var entry in tx.toList)
            {
                Node.walletState.setWalletBalance(entry.Key, entry.Value, ws_snapshot);
            }

            if (!ws_snapshot)
            {
                setAppliedFlag(tx.id, block.blockNum);
            }

            return true;
        }

        // Checks if a transaction is a staking transaction and applies it.
        // Returns true if it's a Staking transaction, otherwise false
        public static bool applyStakingTransaction(Transaction tx, Block block, List<Transaction> failed_transactions, List<byte[]> blockStakers, bool ws_snapshot = false)
        {
            if (tx.type != (int)Transaction.Type.StakingReward)
            {
                return false;
            }

            foreach (var toEntry in tx.toList)
            {
                // Check if the staker's transaction has already been processed
                bool valid = true;
                if(blockStakers.Exists(x => x.SequenceEqual(toEntry.Key)))
                {
                    valid = false;
                }

                // If there's another staking transaction for the staker in this block, ignore
                if (valid == false)
                {
                    Logging.error(String.Format("There's a duplicate staker transaction {0}.", tx.id));
                    failed_transactions.Add(tx);
                    return true;
                }

                Wallet staking_wallet = Node.walletState.getWallet(toEntry.Key, ws_snapshot);
                IxiNumber staking_balance_before = staking_wallet.balance;

                IxiNumber tx_amount = toEntry.Value;

                if (tx_amount < new IxiNumber(new System.Numerics.BigInteger(1)))
                {
                    Logging.error(String.Format("Staking transaction {0} does not have a positive amount.", tx.id));
                    failed_transactions.Add(tx);
                    return true;
                }

                // Check if the transaction is in the sigfreeze
                // TODO: refactor this and make it more efficient
                ulong blocknum = BitConverter.ToUInt64(tx.data, 0);
                // Verify the staking transaction is accurate
                Block targetBlock = Node.blockChain.getBlock(blocknum);
                if (targetBlock == null)
                {
                    failed_transactions.Add(tx);
                    return true;
                }

                valid = false;
                List<byte[]> signatureWallets = targetBlock.getSignaturesWalletAddresses();
                foreach (byte[] wallet_addr in signatureWallets)
                {
                    if (toEntry.Key.SequenceEqual(wallet_addr))
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

                Node.walletState.setWalletBalance(toEntry.Key, staking_balance_after, ws_snapshot);

                blockStakers.Add(toEntry.Key);
            }

            if (!ws_snapshot)
            {
                setAppliedFlag(tx.id, block.blockNum);
            }

            return true;
        }

        // Rolls back a normal transaction
        public static bool rollBackNormalTransaction(Transaction tx)
        {
            // TODO TODO TODO TODO implement this eventually
            // Calculate the transaction amount without fee
            IxiNumber txAmountWithoutFee = tx.amount - tx.fee;

            //Wallet source_wallet = Node.walletState.getWallet(tx.from);
            //Wallet dest_wallet = Node.walletState.getWallet(tx.to);

            //IxiNumber source_balance_before = source_wallet.balance;
            //IxiNumber dest_balance_before = dest_wallet.balance;

            // Withdraw the full amount, including fee
            //IxiNumber source_balance_after = source_balance_before + tx.amount + tx.fee;

            // Deposit the amount without fee, as the fee is distributed by the network a few blocks later
            //IxiNumber dest_balance_after = dest_balance_before - tx.amount;

            // Update the walletstate
            //Node.walletState.setWalletBalance(tx.from, source_balance_after, false);
            //Node.walletState.setWalletBalance(tx.to, dest_balance_after, false);

            return true;
        }

        public static List<string> applyMultisigTransaction(Transaction tx, Block block, List<Transaction> failed_transactions, bool ws_snapshot = false)
        {
            if (tx.type == (int)Transaction.Type.MultisigTX)
            {
                ulong minBh = 0;
                if (block.blockNum > ConsensusConfig.getRedactedWindowSize(block.version))
                {
                    minBh = block.blockNum - ConsensusConfig.getRedactedWindowSize(block.version);
                }
                // Check the block height
                if (minBh > tx.blockHeight || tx.blockHeight > block.blockNum)
                {
                    Logging.warn(String.Format("Incorrect block height for transaction {0}. Tx block height is {1}, expecting at least {2} and at most {3}", tx.id, tx.blockHeight, minBh, block.blockNum + 5));
                    failed_transactions.Add(tx);
                    return null;
                }

                if (tx.fromList.Count > 1)
                {
                    Logging.error(String.Format("Multisig transaction {{ {0} }} has more than one 'from' address!", tx.id));
                    failed_transactions.Add(tx);
                    return null;
                }
                object multisig_type = tx.GetMultisigData();
                byte[] from_address = (new Address(tx.pubKey, tx.fromList.First().Key)).address;
                Wallet orig = Node.walletState.getWallet(from_address, ws_snapshot);
                if (orig is null)
                {
                    Logging.error(String.Format("Multisig transaction {{ {0} }} names a non-existent wallet {1}.", tx.id, Crypto.hashToString(from_address)));
                    failed_transactions.Add(tx);
                    return null;
                }
                if (orig.type != WalletType.Multisig)
                {
                    Logging.error(String.Format("Attempted to apply a multisig TX where the originating wallet is not a multisig wallet! Wallet: {0}, Transaction: {{ {1} }}.",
                        Crypto.hashToString(from_address), tx.id));
                    failed_transactions.Add(tx);
                    return null;
                }

                List<string> related_tx_ids = new List<string>();
                // we only attempt to execute the original transaction, if there are enough signatures
                if (multisig_type is Transaction.MultisigTxData)
                {
                    var multisig_obj = (Transaction.MultisigTxData)multisig_type;
                    if (multisig_obj.origTXId == "")
                    {
                        related_tx_ids = getRelatedMultisigTransactions(tx.id, block);
                        // +1, because the search will not find the current transaction, only the ones related to it
                        int num_multisig_txs = related_tx_ids.Count + 1;
                        if (num_multisig_txs < orig.requiredSigs)
                        {
                            Logging.error(String.Format("Multisig transaction {{ {0} }} doesn't have enough signatures!", tx.id));
                            // ignore if it doesn't have enough sigs - it will either accumulate more sigs, or be pruned after a timeout period but still reject the block
                            return null;
                        }

                        byte[] signer_address = ((new Address(multisig_obj.signerPubKey, multisig_obj.signerNonce)).address);
                        if (!orig.isValidSigner(signer_address))
                        {
                            Logging.warn(String.Format("Tried to use Multisig transaction {{ {0} }} without being an actual owner {1}!",
                                orig.id, Base58Check.Base58CheckEncoding.EncodePlain(signer_address)));
                            failed_transactions.Add(tx);
                            return null;
                        }
                    }
                    else if (multisig_obj.origTXId != "")
                    {
                        // this is a related transaction, which we ignore, because all processing is done on the origin transaction
                        return null;
                    }
                    else
                    {
                        Logging.error(String.Format("Multisig transaction {{ {0} }} has invalid multisig data!", tx.id));
                        failed_transactions.Add(tx);
                        return null;
                    }
                }
                else
                {
                    Logging.error(String.Format("Multisig transaction {{ {0} }} has invalid multisig data!", tx.id));
                    failed_transactions.Add(tx);
                    return null;
                }
                // it processes as normal
                if(applyNormalTransaction(tx, block, failed_transactions, ws_snapshot))
                {
                    return related_tx_ids;
                }
            }
            return null;
        }

        public static bool applyMultisigRelatedTransactions(List<string> related_tx_ids, Block block, List<Transaction> failed_transactions, bool ws_snapshot = false)
        {
            foreach (string txid in related_tx_ids)
            {
                Transaction tx = getTransaction(txid);
                if (tx.type == (int)Transaction.Type.MultisigAddTxSignature)
                {
                    ulong minBh = 0;
                    if (block.blockNum > ConsensusConfig.getRedactedWindowSize(block.version))
                    {
                        minBh = block.blockNum - ConsensusConfig.getRedactedWindowSize(block.version);
                    }
                    // Check the block height
                    if (minBh > tx.blockHeight || tx.blockHeight > block.blockNum)
                    {
                        Logging.warn(String.Format("Incorrect block height for multisig transaction {0}. Tx block height is {1}, expecting at least {2} and at most {3}", tx.id, tx.blockHeight, minBh, block.blockNum + 5));
                        failed_transactions.Add(tx);
                        return false;
                    }

                    if (tx.fromList.Count > 1)
                    {
                        Logging.error(String.Format("Multisig transaction {{ {0} }} has more than one 'from' address!", tx.id));
                        failed_transactions.Add(tx);
                        return false;
                    }

                    foreach (var entry in tx.fromList)
                    {
                        byte[] tmp_address = (new Address(tx.pubKey, entry.Key)).address;

                        Wallet source_wallet = Node.walletState.getWallet(tmp_address, ws_snapshot);
                        IxiNumber source_balance_before = source_wallet.balance;
                        if (source_balance_before < entry.Value)
                        {
                            Logging.warn(String.Format("Multisig transaction {{ {0} }} in block #{1} ({2}) would take wallet {3} below zero.",
                                tx.id, block.blockNum, Crypto.hashToString(block.lastBlockChecksum), tmp_address));
                            failed_transactions.Add(tx);
                            return false;
                        }
                        // Withdraw the full amount, including fee
                        IxiNumber source_balance_after = source_balance_before - entry.Value;

                        Node.walletState.setWalletBalance(tmp_address, source_balance_after, ws_snapshot);
                    }

                    if (!ws_snapshot)
                    {
                        setAppliedFlag(tx.id, block.blockNum);
                    }
                }else
                {
                    Logging.warn(String.Format("Error processing multisig transaction {{ {0} }} in block #{1}, expecting type MultisigAddTxSignature, received {2}.",
                        tx.id, block.blockNum, tx.type));
                    failed_transactions.Add(tx);
                    return false;
                }
            }
            return true;
        }

        public static List<string> applyMultisigChangeTransaction(Transaction tx, Block block, List<Transaction> failed_transactions, bool ws_snapshot = false)
        {
            if (tx.type == (int)Transaction.Type.ChangeMultisigWallet)
            {
                ulong minBh = 0;
                if (block.blockNum > ConsensusConfig.getRedactedWindowSize(block.version))
                {
                    minBh = block.blockNum - ConsensusConfig.getRedactedWindowSize(block.version);
                }
                // Check the block height
                if (minBh > tx.blockHeight || tx.blockHeight > block.blockNum)
                {
                    Logging.warn(String.Format("Incorrect block height for transaction {0}. Tx block height is {1}, expecting at least {2} and at most {3}", tx.id, tx.blockHeight, minBh, block.blockNum + 5));
                    failed_transactions.Add(tx);
                    return null;
                }

                object multisig_type = tx.GetMultisigData();
                byte[] target_wallet_address = (new Address(tx.pubKey, tx.fromList.First().Key)).address;
                Wallet orig = Node.walletState.getWallet(target_wallet_address, ws_snapshot);
                if (orig is null)
                {
                    Logging.error(String.Format("Multisig change transaction {{ {0} }} names a non-existent wallet {1}.", tx.id, Base58Check.Base58CheckEncoding.EncodePlain(target_wallet_address)));
                    failed_transactions.Add(tx);
                    return null;
                }
                List<string> related_tx_ids = getRelatedMultisigTransactions(tx.id, block);
                // +1 because this current transaction will not be found by the search
                int num_valid_sigs = related_tx_ids.Count + 1;
                if (num_valid_sigs < orig.requiredSigs)
                {
                    Logging.info(String.Format("Transaction {{ {0} }} has {1} valid signatures out of required {2}.", tx.id, num_valid_sigs, orig.requiredSigs));
                    return null;
                }

                if (multisig_type is Transaction.MultisigAddrAdd)
                {
                    var multisig_obj = (Transaction.MultisigAddrAdd)multisig_type;

                    byte[] signer_address = ((new Address(multisig_obj.signerPubKey, multisig_obj.signerNonce)).address);
                    if (!orig.isValidSigner(signer_address))
                    {
                        Logging.warn(String.Format("Tried to use Multisig transaction {{ {0} }} without being an actual owner {1}!",
                            orig.id, Base58Check.Base58CheckEncoding.EncodePlain(signer_address)));
                        failed_transactions.Add(tx);
                        return null;
                    }

                    if (orig.isValidSigner(multisig_obj.addrToAdd))
                    {
                        Logging.warn(String.Format("Pubkey {0} is already in allowed multisig list for wallet {1}.", Base58Check.Base58CheckEncoding.EncodePlain(multisig_obj.addrToAdd), Base58Check.Base58CheckEncoding.EncodePlain(orig.id)));
                        failed_transactions.Add(tx);
                        return null;
                    }

                    Logging.info(String.Format("Adding multisig address {0} to wallet {1}.", Base58Check.Base58CheckEncoding.EncodePlain(multisig_obj.addrToAdd), Base58Check.Base58CheckEncoding.EncodePlain(orig.id)));
                    orig.addValidSigner(multisig_obj.addrToAdd);
                    orig.type = WalletType.Multisig;
                    Node.walletState.setWallet(orig, ws_snapshot);
                }
                else if (multisig_type is Transaction.MultisigAddrDel)
                {
                    if (orig.type != WalletType.Multisig)
                    {
                        Logging.error(String.Format("Attempted to execute a multisig change transaction {{ {0} }} on a non-multisig wallet {1}!",
                            tx.id, Crypto.hashToString(orig.id)));
                        failed_transactions.Add(tx);
                        return null;
                    }
                    var multisig_obj = (Transaction.MultisigAddrDel)multisig_type;

                    byte[] signer_address = ((new Address(multisig_obj.signerPubKey, multisig_obj.signerNonce)).address);
                    if (!orig.isValidSigner(signer_address))
                    {
                        Logging.warn(String.Format("Tried to use Multisig transaction {{ {0} }} without being an actual owner {1}!",
                            orig.id, Base58Check.Base58CheckEncoding.EncodePlain(signer_address)));
                        failed_transactions.Add(tx);
                        return null;
                    }

                    if (multisig_obj.addrToDel.SequenceEqual(orig.id))
                    {
                        Logging.error(String.Format("Attempted to remove wallet owner ({0}) from the multisig wallet!", Base58Check.Base58CheckEncoding.EncodePlain(multisig_obj.addrToDel)));
                        failed_transactions.Add(tx);
                        return null;
                    }

                    if (orig.requiredSigs > orig.countAllowedSigners)
                    {
                        Logging.info(String.Format("Removing a signer would make using the wallet impossible. Adjusting required signatures: {0} -> {1}.",
                            orig.requiredSigs, orig.allowedSigners.Length));
                        orig.requiredSigs = (byte)orig.allowedSigners.Length;
                    }
                    Logging.info(String.Format("Removing multisig address {0} from wallet {1}.", Base58Check.Base58CheckEncoding.EncodePlain(multisig_obj.addrToDel), Base58Check.Base58CheckEncoding.EncodePlain(orig.id)));
                    orig.delValidSigner(multisig_obj.addrToDel);
                    if (orig.countAllowedSigners <= 0)
                    {
                        Logging.info(String.Format("Wallet {0} changes back to a single-sig wallet.", Base58Check.Base58CheckEncoding.EncodePlain(orig.id)));
                        orig.type = WalletType.Normal;
                        orig.allowedSigners = null;
                    }
                    Node.walletState.setWallet(orig, ws_snapshot);
                }
                else if (multisig_type is Transaction.MultisigChSig)
                {
                    var multisig_obj = (Transaction.MultisigChSig)multisig_type;
                    if (orig.type != WalletType.Multisig)
                    {
                        Logging.error(String.Format("Attempted to execute a multisig change transaction {{ {0} }} on a non-multisig wallet {1}!",
                            tx.id, Crypto.hashToString(orig.id)));
                        failed_transactions.Add(tx);
                        return null;
                    }

                    byte[] signer_address = ((new Address(multisig_obj.signerPubKey, multisig_obj.signerNonce)).address);
                    if (!orig.isValidSigner(signer_address))
                    {
                        Logging.warn(String.Format("Tried to use Multisig transaction {{ {0} }} without being an actual owner {1}!",
                            orig.id, Base58Check.Base58CheckEncoding.EncodePlain(signer_address)));
                        failed_transactions.Add(tx);
                        return null;
                    }

                    // +1 because "allowedSigners" will contain addresses distinct from the wallet owner, but wallet owner is also one of the permitted signers
                    if (multisig_obj.reqSigs > orig.allowedSigners.Length + 1)
                    {
                        Logging.error(String.Format("Attempted to set required sigs for a multisig wallet to a larger value than the number of allowed pubkeys! Pubkeys = {0}, reqSigs = {1}.",
                            orig.allowedSigners.Length, multisig_obj.reqSigs));
                        failed_transactions.Add(tx);
                        return null;
                    }
                    Logging.info(String.Format("Changing multisig wallet {0} required sigs {1} -> {2}.", Base58Check.Base58CheckEncoding.EncodePlain(orig.id), orig.requiredSigs, multisig_obj.reqSigs));
                    orig.requiredSigs = multisig_obj.reqSigs;
                    Node.walletState.setWallet(orig, ws_snapshot);
                }

                foreach (var entry in tx.fromList)
                {
                    byte[] tmp_address = (new Address(tx.pubKey, entry.Key)).address;

                    Wallet source_wallet = Node.walletState.getWallet(tmp_address, ws_snapshot);
                    IxiNumber source_balance_before = source_wallet.balance;
                    // Withdraw the full amount, including fee
                    IxiNumber source_balance_after = source_balance_before - entry.Value;
                    if (source_balance_after < (long)0)
                    {
                        Logging.warn(String.Format("Transaction {{ {0} }} in block #{1} ({2}) would take wallet {3} below zero.",
                            tx.id, block.blockNum, Crypto.hashToString(block.lastBlockChecksum), tmp_address));
                        failed_transactions.Add(tx);
                        return null;
                    }

                    Node.walletState.setWalletBalance(tmp_address, source_balance_after, ws_snapshot);
                }

                if (!ws_snapshot)
                {
                    setAppliedFlag(tx.id, block.blockNum);
                }
                return related_tx_ids;
            }
            return null;
        }


        // Applies a normal transaction
        public static bool applyNormalTransaction(Transaction tx, Block block, List<Transaction> failed_transactions, bool ws_snapshot = false)
        {
            ulong minBh = 0;
            if (block.blockNum > ConsensusConfig.getRedactedWindowSize(block.version))
            {
                minBh = block.blockNum - ConsensusConfig.getRedactedWindowSize(block.version);
            }
            // Check the block height
            if (minBh > tx.blockHeight || tx.blockHeight > block.blockNum)
            {
                Logging.warn(String.Format("Incorrect block height for transaction {0}. Tx block height is {1}, expecting at least {2} and at most {3}", tx.id, tx.blockHeight, minBh, block.blockNum + 5));
                failed_transactions.Add(tx);
                return false;
            }

            if(!verifyPremineTransaction(tx))
            {
                Logging.warn(String.Format("Tried to spent too much premine, too early - transaction {0}.", tx.id));
                failed_transactions.Add(tx);
                return false;
            }

            // Check if the fee covers the current network minimum fee
            // TODO: adjust this dynamically

            IxiNumber expectedFee = tx.calculateMinimumFee(ConsensusConfig.transactionPrice);
            if (tx.version == 0)
            {
                expectedFee = ConsensusConfig.transactionPrice;
            }
            if (tx.fee - expectedFee < (long)0)
            {
                Logging.error(String.Format("Transaction {{ {0} }} cannot pay minimum fee", tx.id));
                failed_transactions.Add(tx);
                return false;
            }

            IxiNumber total_amount = 0;
            foreach (var entry in tx.fromList)
            {
                byte[] tmp_address = (new Address(tx.pubKey, entry.Key)).address;

                Wallet source_wallet = Node.walletState.getWallet(tmp_address, ws_snapshot);
                IxiNumber source_balance_before = source_wallet.balance;
                // Withdraw the full amount, including fee
                IxiNumber source_balance_after = source_balance_before - entry.Value;
                if (source_balance_after < (long)0)
                {
                    Logging.warn(String.Format("Transaction {{ {0} }} in block #{1} ({2}) would take wallet {3} below zero.",
                        tx.id, block.blockNum, Crypto.hashToString(block.lastBlockChecksum), tmp_address));
                    failed_transactions.Add(tx);
                    return false;
                }

                Node.walletState.setWalletBalance(tmp_address, source_balance_after, ws_snapshot);
                total_amount += entry.Value;
            }

            if (tx.amount + tx.fee != total_amount)
            {
                Logging.error(String.Format("Transaction {{ {0} }}'s input values are different than the total amount + fee", tx.id));
                failed_transactions.Add(tx);
                return false;
            }


            total_amount = 0;
            foreach (var entry in tx.toList)
            {
                Wallet dest_wallet = Node.walletState.getWallet(entry.Key, ws_snapshot);
                IxiNumber dest_balance_before = dest_wallet.balance;


                // Deposit the amount without fee, as the fee is distributed by the network a few blocks later
                IxiNumber dest_balance_after = dest_balance_before + entry.Value;


                // Update the walletstate
                Node.walletState.setWalletBalance(entry.Key, dest_balance_after, ws_snapshot);
                total_amount += entry.Value;
            }

            if (tx.amount != total_amount)
            {
                Logging.error(String.Format("Transaction {{ {0} }}'s output values are different than the total amount", tx.id));
                failed_transactions.Add(tx);
                return false;
            }

            if (!ws_snapshot)
            {
                setAppliedFlag(tx.id, block.blockNum);
            }

            return true;
        }

        // Go through a dictionary of block numbers and respective miners and reward them
        public static void rewardMiners(ulong sent_block_num, IDictionary<ulong, List<object[]>> blockSolutionsDictionary, bool ws_snapshot = false)
        {
            for (int i = 0; i < blockSolutionsDictionary.Count; i++)
            {
                ulong blockNum = blockSolutionsDictionary.Keys.ElementAt(i);

                // Stop rewarding miners after 5th year
                if(blockNum >= 5256000)
                {
                    continue;
                }

                Block block = Node.blockChain.getBlock(blockNum);
                // Check if the block is valid
                if (block == null)
                {
                    Logging.error("PoW target block {0} not found", blockNum);
                    continue;
                }

                List<object[]> miners_to_reward = blockSolutionsDictionary[blockNum];

                IxiNumber miners_count = new IxiNumber(miners_to_reward.Count);

                IxiNumber pow_reward = Miner.calculateRewardForBlock(blockNum);
                IxiNumber powRewardPart = pow_reward / miners_count;

                //Logging.info(String.Format("Rewarding {0} IXI to block #{1} miners", powRewardPart.ToString(), blockNum));

                foreach (var entry in miners_to_reward)
                {
                    // TODO add another address checksum here, just in case
                    // Update the wallet state
                    Wallet miner_wallet = Node.walletState.getWallet((byte[])entry[0], ws_snapshot);
                    IxiNumber miner_balance_before = miner_wallet.balance;
                    IxiNumber miner_balance_after = miner_balance_before + powRewardPart;
                    Node.walletState.setWalletBalance(miner_wallet.id, miner_balance_after, ws_snapshot);

                    if (miner_wallet.id.SequenceEqual(Node.walletStorage.getPrimaryAddress()))
                    {
                        ActivityStorage.updateValue(Encoding.UTF8.GetBytes(((Transaction)entry[2]).id), powRewardPart);
                    }

                }

                // Ignore during snapshot
                if (ws_snapshot == false)
                {
                    // Set the powField as a checksum of all miners for this block
                    block.powField = BitConverter.GetBytes(sent_block_num);
                    Node.blockChain.updateBlock(block);
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
#if TRACE_MEMSTREAM_SIZES
                        Logging.info(String.Format("TransactionPool::getBytes: {0}", m.Length));
#endif
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

        public static bool hasTransaction(string txid)
        {
            lock(transactions)
            {
                return transactions.ContainsKey(txid);
            }
        }

        public static void performCleanup()
        {
            if(Node.blockSync.synchronizing)
            {
                return;
            }

            ulong minBlockHeight = 1;
            if (Node.blockChain.getLastBlockNum() > ConsensusConfig.getRedactedWindowSize())
            {
                minBlockHeight = Node.blockChain.getLastBlockNum() - ConsensusConfig.getRedactedWindowSize();
            }

            lock (transactions)
            {
                var txList = transactions.Select(e => e.Value).Where(x => x != null && x.applied == 0 && x.type == (int)Transaction.Type.PoWSolution).ToArray();
                foreach (var entry in txList)
                {
                    ulong blocknum = 0;
                    try
                    {
                        // Extract the block number and nonce
                        using (MemoryStream m = new MemoryStream(entry.data))
                        {
                            using (BinaryReader reader = new BinaryReader(m))
                            {
                                blocknum = reader.ReadUInt64();
                            }
                        }

                        Block block = Node.blockChain.getBlock(blocknum);

                        if (block == null || block.powField != null)
                        {
                            if (Node.walletStorage.isMyAddress((new Address(entry.pubKey)).address))
                            {
                                ActivityStorage.updateStatus(Encoding.UTF8.GetBytes(entry.id), ActivityStatus.Error, 0);
                            }
                            transactions.Remove(entry.id);
                        }
                    }
                    catch (Exception e)
                    {
                        Logging.error("Exception occured in transactionPool.cleanUp() " + e);
                        // remove invalid/corrupt transaction
                        transactions.Remove(entry.id);
                    }
                }

                txList = transactions.Values.Where(x => x != null && x.applied == 0 && x.blockHeight < minBlockHeight).ToArray();
                foreach (var entry in txList)
                {
                    transactions.Remove(entry.id);
                }
            }
        }


        public static void processPendingTransactions()
        {
            // TODO TODO this has to be refactored and moved to PendingTransactions
            ulong last_block_height = IxianHandler.getLastBlockHeight();
            lock (transactions) // this lock must be here to prevent deadlocks TODO: improve this at some point
            {
                lock (PendingTransactions.pendingTransactions)
                {
                    long cur_time = Clock.getTimestamp();
                    List<object[]> tmp_pending_transactions = new List<object[]>(PendingTransactions.pendingTransactions);
                    int idx = 0;
                    foreach (var entry in tmp_pending_transactions)
                    {
                        Transaction t = (Transaction)entry[0];
                        long tx_time = (long)entry[1];
                        if ((int)entry[2] > 3) // already received 3+ feedback
                        {
                            continue;
                        }

                        if (t.applied != 0)
                        {
                            PendingTransactions.pendingTransactions.RemoveAll(x => ((Transaction)x[0]).id.SequenceEqual(t.id));
                            continue;
                        }

                        // if transaction expired, remove it from pending transactions
                        if (last_block_height > ConsensusConfig.getRedactedWindowSize() && t.blockHeight < last_block_height - ConsensusConfig.getRedactedWindowSize())
                        {
                            ActivityStorage.updateStatus(Encoding.UTF8.GetBytes(t.id), ActivityStatus.Error, 0);
                            PendingTransactions.pendingTransactions.RemoveAll(x => ((Transaction)x[0]).id.SequenceEqual(t.id));
                            continue;
                        }

                        // check if PoW and if already solved
                        if (t.type == (int)Transaction.Type.PoWSolution)
                        {
                            ulong pow_block_num = BitConverter.ToUInt64(t.data, 0);

                            Block tmpBlock = Node.blockChain.getBlock(pow_block_num);
                            if (tmpBlock == null || tmpBlock.powField != null)
                            {
                                ActivityStorage.updateStatus(Encoding.UTF8.GetBytes(t.id), ActivityStatus.Error, 0);
                                PendingTransactions.pendingTransactions.RemoveAll(x => ((Transaction)x[0]).id.SequenceEqual(t.id));
                                continue;
                            }
                        }
                        else
                        {
                            // check if transaction is still valid
                            if (getTransaction(t.id) == null && !verifyTransaction(t))
                            {
                                ActivityStorage.updateStatus(Encoding.UTF8.GetBytes(t.id), ActivityStatus.Error, 0);
                                PendingTransactions.pendingTransactions.RemoveAll(x => ((Transaction)x[0]).id.SequenceEqual(t.id));
                                continue;
                            }
                        }

                        if ((bool)PendingTransactions.pendingTransactions[idx][3] == false && cur_time - tx_time > 20) // if the transaction is pending for over 20 seconds, send inquiry
                        {
                            ProtocolMessage.broadcastGetTransaction(t.id, 0);
                            PendingTransactions.pendingTransactions[idx][3] = true;
                        }

                        if (cur_time - tx_time > 40) // if the transaction is pending for over 40 seconds, resend
                        {
                            CoreProtocolMessage.broadcastProtocolMessage(new char[] { 'M', 'H' }, ProtocolMessageCode.newTransaction, t.getBytes(), null);
                            PendingTransactions.pendingTransactions[idx][1] = cur_time;
                        }
                        idx++;
                    }
                }
            }
        }

        public static long getTransactionCount()
        {
            lock(transactions)
            {
                return transactions.LongCount();
            }
        }

        public static void compactTransactionsForBlock(Block b)
        {
            lock (transactions)
            {
                foreach (var entry in b.transactions)
                {
                    transactions[entry] = null;
                }
            }
        }

        public static bool removeTransaction(string txid)
        {
            lock(transactions)
            {
                return transactions.Remove(txid);
            }
        }

        // Returns a list of transactions connected to this block 
        public static List<Transaction> getFullBlockTransactions(Block block)
        {
            List<Transaction> tx_list = new List<Transaction>();
            List<string> tx_ids = block.transactions;
            for (int i = 0; i < tx_ids.Count; i++)
            {
                Transaction t = getTransaction(tx_ids[i], block.blockNum);
                if (t == null)
                {
                    Logging.error(string.Format("nulltx: {0}", tx_ids[i]));
                    continue;
                }
                tx_list.Add(t);
            }
            return tx_list;
        }

        // temporary function that will correctly JSON Serialize IxiNumber
        public static List<Dictionary<string, object>> getFullBlockTransactionsAsArray(Block block)
        {
            List<Dictionary<string, object>> tx_list = new List<Dictionary<string, object>>();
            List<string> tx_ids = block.transactions;
            for (int i = 0; i < tx_ids.Count; i++)
            {
                Transaction t = TransactionPool.getTransaction(tx_ids[i], block.blockNum, true);
                if (t == null)
                {
                    Logging.error(string.Format("nulltx: {0}", tx_ids[i]));
                    continue;
                }

                tx_list.Add(t.toDictionary());

            }
            return tx_list;
        }

        // Returs total value of transactions connected to this block 
        public static IxiNumber getTotalTransactionsValueInBlock(Block block)
        {
            IxiNumber val = 0;
            List<string> tx_ids = block.transactions;
            for (int i = 0; i < tx_ids.Count; i++)
            {
                Transaction t = TransactionPool.getTransaction(tx_ids[i], block.blockNum);
                if (t == null)
                    Logging.error(string.Format("nulltx: {0}", tx_ids[i]));
                else
                    val.add(t.amount);
            }
            return val;
        }
    }
}
